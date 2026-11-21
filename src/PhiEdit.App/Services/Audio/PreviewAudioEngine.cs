using NAudio.CoreAudioApi;
using NAudio.Wave;
using PhiEdit.Core.Charts;
using PhiEdit.Core.Editing;
using PhiEdit.Core.Preview;
using PhiEdit.Core.Skins;

namespace PhiEdit.App.Services.Audio;

/// <summary>
/// 预览音频引擎。参考 PhiEdit Base：背景音与命中音都作为独立音源交给标准混音器，
/// 由系统音频会话统一输出；命中音按谱面采样点插入，音量随系统音量。
/// </summary>
public sealed class PreviewAudioEngine : IPreviewTimeSource, IDisposable
{
    private readonly ScheduledAudioMixer _mixer = new();
    private IWavePlayer? _output;
    private double _rate = 1;
    private bool _playing;
    private bool _disposed;
    private int _loadRevision;

    public bool IsLoaded => _mixer.IsLoaded;

    public bool IsPlaying => _playing;

    public double CurrentSeconds => _mixer.PositionSeconds;

    public double DurationSeconds => _mixer.DurationSeconds;

    public double Rate
    {
        get => _rate;
        set
        {
            _rate = double.IsFinite(value) && value > 0 ? value : 1;
            _mixer.SetRate(_rate);
        }
    }

    public IReadOnlyList<LineAudioBus> Buses { get; private set; } = Array.Empty<LineAudioBus>();

    public string Status { get; private set; } = "音频未加载";

    public string OutputKind { get; private set; } = "未初始化";

    public async Task LoadAsync(EditorSession session, NoteSkin? skin)
    {
        var revision = Interlocked.Increment(ref _loadRevision);
        Status = "音频加载中";
        var data = await Task.Run(() => BuildData(session, skin));
        if (revision != _loadRevision || _disposed)
        {
            return;
        }

        StopOutput();
        _mixer.Configure(data.Music, data.MusicOffsetSamples, data.Sounds, data.Hits, data.Buses);
        Buses = data.Buses;
        _output = CreateOutput();
        _output.Init(_mixer.ToWaveProvider());
        _playing = false;

        var hitCount = data.Hits.Sum(hit => hit.Count);
        Status = data.Music is null
            ? $"背景音缺失 · 命中音 {hitCount} · {OutputKind}"
            : $"音频就绪 · 命中音 {hitCount} · {OutputKind}";
    }

    public void Play()
    {
        if (!IsLoaded || _disposed || _playing)
        {
            return;
        }

        _output ??= CreateOutput();
        _output.Play();
        _playing = true;
    }

    public void Pause()
    {
        if (!_playing)
        {
            return;
        }

        _output?.Stop();
        _playing = false;
    }

    public void Seek(double seconds) => _mixer.Seek(seconds);

    public void SetVolumes(float musicVolume, float hitVolume) =>
        _mixer.SetVolumes(musicVolume, hitVolume);

    public void SetBus(int lineIndex, float volume, bool muted, bool solo) =>
        _mixer.SetBus(lineIndex, volume, muted, solo);

    private IWavePlayer CreateOutput()
    {
        try
        {
            var output = new WasapiOut(AudioClientShareMode.Shared, false, 100);
            OutputKind = "系统共享输出";
            return output;
        }
        catch
        {
            OutputKind = "WaveOut 回退";
            return new WaveOutEvent
            {
                DesiredLatency = 120,
                NumberOfBuffers = 4,
            };
        }
    }

    private void StopOutput()
    {
        _output?.Stop();
        _output?.Dispose();
        _output = null;
        _playing = false;
    }

    private static AudioData BuildData(EditorSession session, NoteSkin? skin)
    {
        var document = session.Document;
        var tempo = document.BuildTempoMap();
        var offsetSeconds = document.OffsetMilliseconds / 1000.0;
        var musicOffsetSamples = (long)Math.Round(offsetSeconds * PcmAudioBuffer.SampleRate);
        var sounds = new List<PcmAudioBuffer>();
        var soundIndices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var buses = Enumerable.Range(0, document.Lines.Count).Select(_ => new LineAudioBus()).ToArray();
        var hitGroups = new Dictionary<(long Time, int Sound), ScheduledHit>();

        int ResolveSound(string name)
        {
            if (soundIndices.TryGetValue(name, out var index))
            {
                return index;
            }

            var bytes = session.Assets.Resolve(name) ?? skin?.GetFile(name);
            if (bytes is null)
            {
                return -1;
            }

            index = sounds.Count;
            sounds.Add(AudioDecoder.Decode(bytes, name));
            soundIndices[name] = index;
            return index;
        }

        foreach (var (line, lineIndex) in document.Lines.Select((line, index) => (line, index)))
        {
            foreach (var note in line.Notes)
            {
                if (note.IsFake)
                {
                    continue;
                }

                var kind = note.Type switch
                {
                    NoteType.Flick => "flick",
                    NoteType.Drag => "drag",
                    _ => "tap",
                };
                var fileName = kind switch
                {
                    "flick" => "flick.ogg",
                    "drag" => "drag.ogg",
                    _ => "click.ogg",
                };
                var requested = string.IsNullOrWhiteSpace(note.HitSound) ? kind : note.HitSound;
                var index = ResolveSound(requested);
                if (index < 0)
                {
                    index = ResolveSound(fileName);
                }

                if (index < 0)
                {
                    continue;
                }

                var seconds = tempo.SecondsAt(note.StartBeat.Value, line.BpmFactor) + offsetSeconds;
                var sample = (long)Math.Round(seconds * PcmAudioBuffer.SampleRate);
                var key = (sample, index);
                if (hitGroups.TryGetValue(key, out var existing))
                {
                    hitGroups[key] = existing with { Count = existing.Count + 1 };
                }
                else
                {
                    hitGroups[key] = new ScheduledHit(sample, index, lineIndex);
                }
            }
        }

        PcmAudioBuffer? music = null;
        var musicName = session.Assets.FindSong(document.SongFilename);
        if (musicName is not null)
        {
            var bytes = session.Assets.Resolve(musicName);
            if (bytes is not null)
            {
                try
                {
                    music = AudioDecoder.Decode(bytes, musicName);
                }
                catch
                {
                    music = null;
                }
            }
        }

        return new AudioData(music, musicOffsetSamples, sounds.ToArray(),
            hitGroups.Values.OrderBy(hit => hit.TimeSamples).ToArray(), buses);
    }

    private sealed record AudioData(
        PcmAudioBuffer? Music,
        long MusicOffsetSamples,
        PcmAudioBuffer[] Sounds,
        ScheduledHit[] Hits,
        LineAudioBus[] Buses);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Increment(ref _loadRevision);
        StopOutput();
    }
}

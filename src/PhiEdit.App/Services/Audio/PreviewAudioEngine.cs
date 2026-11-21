using NAudio.CoreAudioApi;
using NAudio.Wave;
using PhiEdit.Core.Charts;
using PhiEdit.Core.Editing;
using PhiEdit.Core.Preview;
using PhiEdit.Core.Skins;

namespace PhiEdit.App.Services.Audio;

/// <summary>
/// 预览音频引擎。参考 PhiEdit Base：背景音乐交给系统原生解码/输出，
/// 命中音单独预解码并按谱面采样点调度；两条链路各自接系统音频会话，音量随系统音量。
/// </summary>
public sealed class PreviewAudioEngine : IPreviewTimeSource, IDisposable
{
    private readonly AudioMixer _hits = new();
    private IWavePlayer? _musicOutput;
    private IWavePlayer? _hitOutput;
    private AudioFileReader? _musicReader;
    private string? _musicTempPath;
    private LineAudioBus[] _buses = Array.Empty<LineAudioBus>();
    private double _offsetSeconds;
    private double _rate = 1;
    private float _musicVolume = 0.75f;
    private float _hitVolume = 0.3f;
    private bool _playing;
    private bool _disposed;
    private int _loadRevision;

    public bool IsLoaded => _musicReader is not null || _hits.IsLoaded;

    public bool IsPlaying => _playing;

    public double CurrentSeconds
    {
        get
        {
            if (_musicReader is not null)
            {
                return Math.Max(0, _musicReader.CurrentTime.TotalSeconds - _offsetSeconds);
            }

            return _hits.PositionSeconds;
        }
    }

    public double DurationSeconds
    {
        get
        {
            if (_musicReader is not null)
            {
                return Math.Max(0, _musicReader.TotalTime.TotalSeconds - _offsetSeconds);
            }

            return 0;
        }
    }

    public double Rate
    {
        get => _rate;
        set
        {
            _rate = double.IsFinite(value) && value > 0 ? value : 1;
            _hits.SetRate((float)_rate);
        }
    }

    public IReadOnlyList<LineAudioBus> Buses => _buses;

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

        StopOutputs();
        DisposeMusic();

        _buses = data.Buses;
        _offsetSeconds = data.OffsetSeconds;
        _hits.Configure(null, 0, data.Sounds, data.Hits, data.Buses);
        _hits.SetVolumes(1, _hitVolume);
        _hits.SetRate((float)_rate);
        OutputKind = "系统共享输出";

        if (data.MusicBytes is { Length: > 0 })
        {
            try
            {
                var extension = Path.GetExtension(data.MusicName ?? string.Empty);
                if (string.IsNullOrWhiteSpace(extension))
                {
                    extension = ".mp3";
                }

                _musicTempPath = Path.Combine(Path.GetTempPath(), $"phiedit-preview-{Guid.NewGuid():N}{extension}");
                File.WriteAllBytes(_musicTempPath, data.MusicBytes);
                _musicReader = new AudioFileReader(_musicTempPath);
                _musicOutput = CreateOutput();
                _musicOutput.Init(_musicReader);
                _musicOutput.Volume = _musicVolume;
            }
            catch
            {
                DisposeMusic();
            }
        }

        _hitOutput = CreateOutput();
        _hitOutput.Init(_hits.ToWaveProvider());
        _hitOutput.Volume = 1;

        Status = data.MusicBytes is null
            ? $"背景音缺失 · 命中音 {data.Hits.Length} · {OutputKind}"
            : $"音频就绪 · 命中音 {data.Hits.Length} · {OutputKind}";
    }

    public void Play()
    {
        if (!IsLoaded || _disposed || _playing)
        {
            return;
        }

        if (DurationSeconds > 0 && CurrentSeconds >= DurationSeconds)
        {
            Seek(0);
        }

        _hits.Seek(CurrentSeconds);
        _musicOutput?.Play();
        _hitOutput?.Play();
        _playing = true;
    }

    public void Pause()
    {
        if (!_playing)
        {
            return;
        }

        _musicOutput?.Stop();
        _hitOutput?.Stop();
        _playing = false;
    }

    public void Seek(double seconds)
    {
        var value = double.IsFinite(seconds) ? Math.Max(0, seconds) : 0;
        if (_musicReader is not null)
        {
            var source = Math.Min(value + _offsetSeconds, Math.Max(0, _musicReader.TotalTime.TotalSeconds - 1e-6));
            _musicReader.CurrentTime = TimeSpan.FromSeconds(source);
        }

        _hits.Seek(value);
    }

    public void SetVolumes(float musicVolume, float hitVolume)
    {
        _musicVolume = Math.Clamp(musicVolume, 0, 1);
        _hitVolume = Math.Clamp(hitVolume, 0, 1);
        if (_musicOutput is not null)
        {
            _musicOutput.Volume = _musicVolume;
        }

        _hits.SetVolumes(1, _hitVolume);
    }

    public void SetBus(int lineIndex, float volume, bool muted, bool solo) =>
        _hits.SetBus(lineIndex, volume, muted, solo);

    private IWavePlayer CreateOutput()
    {
        try
        {
            return new WasapiOut(AudioClientShareMode.Shared, false, 100);
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

    private void StopOutputs()
    {
        _musicOutput?.Stop();
        _hitOutput?.Stop();
        _musicOutput?.Dispose();
        _hitOutput?.Dispose();
        _musicOutput = null;
        _hitOutput = null;
        _playing = false;
    }

    private void DisposeMusic()
    {
        _musicReader?.Dispose();
        _musicReader = null;
        if (_musicTempPath is not null && File.Exists(_musicTempPath))
        {
            try
            {
                File.Delete(_musicTempPath);
            }
            catch
            {
            }
        }

        _musicTempPath = null;
    }

    private static AudioData BuildData(EditorSession session, NoteSkin? skin)
    {
        var document = session.Document;
        var tempo = document.BuildTempoMap();
        var offsetSeconds = document.OffsetMilliseconds / 1000.0;
        var sounds = new List<PcmAudioBuffer>();
        var soundIndices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var buses = Enumerable.Range(0, document.Lines.Count).Select(_ => new LineAudioBus()).ToArray();
        var hits = new List<ScheduledHit>();

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

        for (var lineIndex = 0; lineIndex < document.Lines.Count; lineIndex++)
        {
            var line = document.Lines[lineIndex];
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
                hits.Add(new ScheduledHit(
                    (long)Math.Round(seconds * PcmAudioBuffer.SampleRate), index, lineIndex));
            }
        }

        byte[]? musicBytes = null;
        string? musicName = null;
        var musicNameInAssets = session.Assets.FindSong(document.SongFilename);
        if (musicNameInAssets is not null)
        {
            musicBytes = session.Assets.Resolve(musicNameInAssets);
            musicName = musicNameInAssets;
        }

        return new AudioData(musicBytes, musicName, sounds.ToArray(),
            hits.OrderBy(hit => hit.TimeSamples).ToArray(), buses, offsetSeconds);
    }

    private sealed record AudioData(
        byte[]? MusicBytes,
        string? MusicName,
        PcmAudioBuffer[] Sounds,
        ScheduledHit[] Hits,
        LineAudioBus[] Buses,
        double OffsetSeconds);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Increment(ref _loadRevision);
        StopOutputs();
        DisposeMusic();
    }
}

using NAudio.Wave;
using PhiEdit.Core.Charts;
using PhiEdit.Core.Editing;
using PhiEdit.Core.Preview;
using PhiEdit.Core.Skins;

namespace PhiEdit.App.Services.Audio;

/// <summary>
/// 低延迟预览音频引擎。背景音乐与每条判定线的命中音分总线混音，
/// 全部 PCM 预解码，命中音按采样点插入；输出走普通系统音频会话，音量由系统控制。
/// </summary>
public sealed class PreviewAudioEngine : IPreviewTimeSource, IDisposable
{
    private readonly AudioMixer _mixer = new();
    private IWavePlayer? _output;
    private int _loadRevision;
    private bool _disposed;
    private double _rate = 1;

    public bool IsLoaded => _mixer.IsLoaded;

    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;

    public double CurrentSeconds => _mixer.PositionSeconds;

    public double DurationSeconds => _mixer.DurationSeconds;

    public double Rate
    {
        get => _rate;
        set
        {
            _rate = value;
            _mixer.SetRate((float)value);
        }
    }

    public IReadOnlyList<LineAudioBus> Buses { get; private set; } = Array.Empty<LineAudioBus>();

    public string Status { get; private set; } = "音频未加载";

    public async Task LoadAsync(EditorSession session, NoteSkin? skin)
    {
        var revision = Interlocked.Increment(ref _loadRevision);
        Status = "音频加载中";
        var result = await Task.Run(() => BuildMixerData(session, skin));
        if (revision != _loadRevision || _disposed)
        {
            return;
        }

        _mixer.Configure(result.Music, session.Document.OffsetMilliseconds, result.Sounds, result.Hits, result.Buses);
        Buses = result.Buses;
        InitializeOutput();
        Status = result.Music is null ? "背景音缺失，仅命中音可用" : "系统输出就绪";
    }

    public void Play()
    {
        if (!IsLoaded || _disposed)
        {
            return;
        }

        if (DurationSeconds > 0 && CurrentSeconds >= DurationSeconds)
        {
            Seek(0);
        }

        _output ??= CreateOutput();
        _output.Play();
    }

    public void Pause()
    {
        if (_output?.PlaybackState == PlaybackState.Playing)
        {
            _output.Stop();
        }
    }

    public void Seek(double seconds)
    {
        _mixer.Seek(double.IsFinite(seconds) ? Math.Max(0, seconds) : 0);
    }

    public void SetVolumes(float musicVolume, float hitVolume) => _mixer.SetVolumes(musicVolume, hitVolume);

    public void SetBus(int lineIndex, float volume, bool muted, bool solo) =>
        _mixer.SetBus(lineIndex, volume, muted, solo);

    private void InitializeOutput()
    {
        _output?.Stop();
        _output?.Dispose();
        _output = CreateOutput();
    }

    private IWavePlayer CreateOutput()
    {
        // 使用普通系统音频输出：与浏览器/播放器共用设备，音量由系统音量混合器控制。
        // 不再使用 WASAPI 独占模式，避免独占设备导致其他程序视频卡顿、且系统音量无效。
        var output = new WaveOutEvent
        {
            DesiredLatency = 60,
            NumberOfBuffers = 3,
        };
        output.Init(new WaveFloatTo16Provider(_mixer.ToWaveProvider()));
        return output;
    }

    private static MixerData BuildMixerData(EditorSession session, NoteSkin? skin)
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

        return new MixerData(music, sounds.ToArray(), hits.ToArray(), buses);
    }

    private sealed record MixerData(
        PcmAudioBuffer? Music,
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
        _output?.Stop();
        _output?.Dispose();
        _output = null;
    }
}

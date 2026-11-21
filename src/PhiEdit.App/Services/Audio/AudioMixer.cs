using NAudio.Wave;

namespace PhiEdit.App.Services.Audio;

/// <summary>单条判定线的音效总线状态，供界面独立控制。</summary>
public sealed class LineAudioBus
{
    public float Volume { get; set; } = 1;
    public bool Muted { get; set; }
    public bool Solo { get; set; }
}

public readonly record struct ScheduledHit(long TimeSamples, int SoundIndex, int LineIndex, int Count = 1);

internal sealed class ActiveVoice
{
    public ActiveVoice(PcmAudioBuffer buffer, long startSample, int lineIndex)
    {
        Buffer = buffer;
        StartSample = startSample;
        LineIndex = lineIndex;
    }

    public PcmAudioBuffer Buffer { get; }

    /// <summary>命中点在谱面时间轴上的绝对采样位置，跨缓冲区保持不变。</summary>
    public long StartSample { get; }

    public int LineIndex { get; }

    /// <summary>已消费的源采样数；按播放倍速递增。</summary>
    public double SourcePosition { get; set; }

    public bool Finished => SourcePosition >= Buffer.Frames;
}

/// <summary>
/// 在音频回调里把预解码音乐与命中音按采样点混合。命中音只从内存 PCM 复制，
/// 避免 UI 定时器触发带来的 16ms 级延迟。
/// </summary>
public sealed class AudioMixer : ISampleProvider
{
    private const int MaxActiveVoices = 192;

    private readonly object _sync = new();
    private readonly List<ActiveVoice> _active = new();
    private ScheduledHit[] _hits = Array.Empty<ScheduledHit>();
    private PcmAudioBuffer[] _sounds = Array.Empty<PcmAudioBuffer>();
    private LineAudioBus[] _buses = Array.Empty<LineAudioBus>();
    private PcmAudioBuffer? _music;
    private long _musicOffsetSamples;
    private long _positionSamples;
    private int _nextHit;
    private float _musicVolume = 0.75f;
    private float _hitVolume = 0.3f;
    private float _rate = 1;

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(PcmAudioBuffer.SampleRate, 2);

    public bool IsLoaded => _music is not null || _sounds.Length > 0;

    public double PositionSeconds => Interlocked.Read(ref _positionSamples) / (double)PcmAudioBuffer.SampleRate;

    public double DurationSeconds
    {
        get
        {
            lock (_sync)
            {
                if (_music is null)
                {
                    return 0;
                }

                return Math.Max(0, _music.DurationSeconds - _musicOffsetSamples / (double)PcmAudioBuffer.SampleRate);
            }
        }
    }

    public void Configure(PcmAudioBuffer? music, double offsetMilliseconds,
        PcmAudioBuffer[] sounds, ScheduledHit[] hits, LineAudioBus[] buses)
    {
        lock (_sync)
        {
            _music = music;
            _musicOffsetSamples = (long)Math.Round(offsetMilliseconds / 1000.0 * PcmAudioBuffer.SampleRate);
            _sounds = sounds;
            _hits = hits.OrderBy(hit => hit.TimeSamples).ToArray();
            _buses = buses;
            _positionSamples = 0;
            _active.Clear();
            _nextHit = 0;
        }
    }

    public void Seek(double chartSeconds)
    {
        lock (_sync)
        {
            var sample = (long)Math.Round(chartSeconds * PcmAudioBuffer.SampleRate);
            _positionSamples = Math.Max(0, sample);
            _active.Clear();
            _nextHit = LowerBound(_hits, _positionSamples);
        }
    }

    public void SetVolumes(float musicVolume, float hitVolume)
    {
        lock (_sync)
        {
            _musicVolume = Math.Clamp(musicVolume, 0, 1);
            _hitVolume = Math.Clamp(hitVolume, 0, 1);
        }
    }

    public void SetRate(float rate)
    {
        lock (_sync)
        {
            _rate = Math.Clamp(rate, 0.25f, 4f);
        }
    }

    public void SetBus(int lineIndex, float volume, bool muted, bool solo)
    {
        lock (_sync)
        {
            if (lineIndex < 0 || lineIndex >= _buses.Length)
            {
                return;
            }

            _buses[lineIndex].Volume = Math.Clamp(volume, 0, 1);
            _buses[lineIndex].Muted = muted;
            _buses[lineIndex].Solo = solo;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        lock (_sync)
        {
            Array.Clear(buffer, offset, count);
            var frames = count / 2;
            if (frames == 0 || !IsLoaded)
            {
                return count;
            }

            var start = _positionSamples;
            var rate = _rate;
            var end = start + (long)Math.Ceiling(frames * rate);

            if (_music is not null)
            {
                var musicGain = _musicVolume;
                for (var frame = 0; frame < frames; frame++)
                {
                    var sourceFrame = start + (long)Math.Round(frame * rate) + _musicOffsetSamples;
                    if (sourceFrame < 0 || sourceFrame >= _music.Frames)
                    {
                        continue;
                    }

                    var index = (int)sourceFrame * 2;
                    buffer[offset + frame * 2] += _music.Samples[index] * musicGain;
                    buffer[offset + frame * 2 + 1] += _music.Samples[index + 1] * musicGain;
                }
            }

            while (_nextHit < _hits.Length && _hits[_nextHit].TimeSamples < end)
            {
                var hit = _hits[_nextHit++];
                if (hit.TimeSamples < start || hit.SoundIndex < 0 || hit.SoundIndex >= _sounds.Length)
                {
                    continue;
                }

                if (_active.Count >= MaxActiveVoices)
                {
                    continue;
                }

                // 命中音按谱面绝对采样点注册；每个缓冲区重新换算相对位置，
                // 否则跨缓冲区时 StartFrame 不再递减，命中音会一直延后甚至听不到。
                _active.Add(new ActiveVoice(_sounds[hit.SoundIndex], hit.TimeSamples, hit.LineIndex));
            }

            var anySolo = _buses.Any(bus => bus.Solo);
            var busGains = new float[_buses.Length];
            for (var index = 0; index < _buses.Length; index++)
            {
                var bus = _buses[index];
                busGains[index] = bus.Muted || (anySolo && !bus.Solo) ? 0 : bus.Volume * _hitVolume;
            }

            for (var voiceIndex = _active.Count - 1; voiceIndex >= 0; voiceIndex--)
            {
                var voice = _active[voiceIndex];
                var source = voice.Buffer.Samples;
                for (var frame = 0; frame < frames; frame++)
                {
                    var chartSample = start + (long)Math.Round(frame * rate);
                    if (chartSample < voice.StartSample || voice.Finished)
                    {
                        continue;
                    }

                    var sourceIndex = (int)voice.SourcePosition;
                    if (sourceIndex >= voice.Buffer.Frames)
                    {
                        break;
                    }

                    var target = offset + frame * 2;
                    var sourceFrame = sourceIndex * 2;
                    var lineGain = voice.LineIndex >= 0 && voice.LineIndex < busGains.Length
                        ? busGains[voice.LineIndex]
                        : _hitVolume;
                    buffer[target] += source[sourceFrame] * lineGain;
                    buffer[target + 1] += source[sourceFrame + 1] * lineGain;
                    voice.SourcePosition += rate;
                }

                if (voice.Finished)
                {
                    _active.RemoveAt(voiceIndex);
                }
            }

            _positionSamples = end;
            return count;
        }
    }

    private static int LowerBound(ScheduledHit[] hits, long sample)
    {
        var low = 0;
        var high = hits.Length;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (hits[middle].TimeSamples < sample)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }
}

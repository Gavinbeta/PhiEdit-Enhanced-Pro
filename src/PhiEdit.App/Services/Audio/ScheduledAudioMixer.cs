using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace PhiEdit.App.Services.Audio;

/// <summary>把预解码 PCM 作为 ISampleProvider 播放，支持起始延迟、增益与采样步进。</summary>
public sealed class PcmSampleProvider : ISampleProvider
{
    private readonly PcmAudioBuffer _buffer;
    private double _position;

    public PcmSampleProvider(PcmAudioBuffer buffer, int startFrame = 0, float gain = 1,
        int delayFrames = 0, double step = 1)
    {
        _buffer = buffer;
        _position = Math.Clamp(startFrame, 0, buffer.Frames);
        Gain = gain;
        DelayFrames = Math.Max(0, delayFrames);
        Step = step is > 0 and <= 8 ? step : 1;
    }

    public int Position => (int)_position;

    public int DelayFrames { get; private set; }

    public float Gain { get; set; }

    public double Step { get; set; }

    public bool Finished => DelayFrames <= 0 && _position >= _buffer.Frames;

    public WaveFormat WaveFormat { get; } =
        WaveFormat.CreateIeeeFloatWaveFormat(PcmAudioBuffer.SampleRate, 2);

    public int Read(float[] buffer, int offset, int count)
    {
        var frames = count / 2;
        for (var frame = 0; frame < frames; frame++)
        {
            if (DelayFrames > 0)
            {
                DelayFrames--;
                continue;
            }

            var index = (int)_position;
            if (index >= _buffer.Frames)
            {
                continue;
            }

            var source = index * 2;
            buffer[offset + frame * 2] = _buffer.Samples[source] * Gain;
            buffer[offset + frame * 2 + 1] = _buffer.Samples[source + 1] * Gain;
            _position += Step;
        }

        return count;
    }
}

/// <summary>
/// 用 NAudio 标准 MixingSampleProvider 混合背景与命中音。
/// 命中音按参考 hitsounds.mjs 的前瞻窗口分组预混成单个音源，
/// 避免密集音符逐个创建声部造成吞音与调度抖动。
/// </summary>
public sealed class ScheduledAudioMixer : ISampleProvider
{
    private const double ScheduleAheadSeconds = 0.25;

    private readonly MixingSampleProvider _mixer =
        new(WaveFormat.CreateIeeeFloatWaveFormat(PcmAudioBuffer.SampleRate, 2)) { ReadFully = true };

    private readonly List<PcmSampleProvider> _active = new();
    private PcmAudioBuffer? _music;
    private PcmAudioBuffer[] _sounds = Array.Empty<PcmAudioBuffer>();
    private ScheduledHit[] _hits = Array.Empty<ScheduledHit>();
    private LineAudioBus[] _buses = Array.Empty<LineAudioBus>();
    private PcmSampleProvider? _musicSource;
    private long _musicOffsetSamples;
    private long _position;
    private int _nextHit;
    private double _rate = 1;
    private float _musicVolume = 0.75f;
    private float _hitVolume = 0.3f;

    private int LookaheadFrames =>
        (int)Math.Round(ScheduleAheadSeconds * PcmAudioBuffer.SampleRate);

    public WaveFormat WaveFormat => _mixer.WaveFormat;

    public bool IsLoaded => _music is not null || _sounds.Length > 0;

    public double PositionSeconds => _position / (double)PcmAudioBuffer.SampleRate;

    public double DurationSeconds => _music?.DurationSeconds ?? 0;

    public void Configure(PcmAudioBuffer? music, long musicOffsetSamples, PcmAudioBuffer[] sounds,
        ScheduledHit[] hits, LineAudioBus[] buses)
    {
        _mixer.RemoveAllMixerInputs();
        _active.Clear();
        _music = music;
        _musicOffsetSamples = musicOffsetSamples;
        _sounds = sounds;
        _hits = hits.OrderBy(hit => hit.TimeSamples).ToArray();
        _buses = buses;
        _position = 0;
        _nextHit = 0;
        _musicSource = music is null
            ? null
            : new PcmSampleProvider(music, (int)Math.Clamp(musicOffsetSamples, 0, music.Frames),
                _musicVolume, 0, _rate);
        if (_musicSource is not null)
        {
            _mixer.AddMixerInput(_musicSource);
        }
    }

    public void Seek(double seconds)
    {
        _position = Math.Max(0, (long)Math.Round(seconds * PcmAudioBuffer.SampleRate));
        _nextHit = LowerBound(_hits, _position);
        _active.Clear();
        _mixer.RemoveAllMixerInputs();
        if (_music is not null)
        {
            var offset = (int)Math.Clamp(_position + _musicOffsetSamples, 0, _music.Frames);
            _musicSource = new PcmSampleProvider(_music, offset, _musicVolume, 0, _rate);
            _mixer.AddMixerInput(_musicSource);
        }
    }

    public void SetRate(double rate)
    {
        _rate = rate is > 0 and < 100 ? rate : 1;
        if (_musicSource is not null)
        {
            _musicSource.Step = _rate;
        }
    }

    public void SetVolumes(float musicVolume, float hitVolume)
    {
        _musicVolume = Math.Clamp(musicVolume, 0, 1);
        _hitVolume = Math.Clamp(hitVolume, 0, 1);
        if (_musicSource is not null)
        {
            _musicSource.Gain = _musicVolume;
        }
    }

    public void SetBus(int lineIndex, float volume, bool muted, bool solo)
    {
        if (lineIndex < 0 || lineIndex >= _buses.Length)
        {
            return;
        }

        _buses[lineIndex].Volume = Math.Clamp(volume, 0, 1);
        _buses[lineIndex].Muted = muted;
        _buses[lineIndex].Solo = solo;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        RemoveFinished();

        var frames = count / 2;
        var scheduleFrames = frames + LookaheadFrames;
        var scheduleEnd = _position + (long)Math.Round(scheduleFrames * _rate);
        var anySolo = false;
        foreach (var bus in _buses)
        {
            if (bus.Solo)
            {
                anySolo = true;
                break;
            }
        }

        // 只把一个前瞻窗口内的命中音按音效预混成少量音源：
        // 静音线直接跳过，不创建 0 音量声部；同音效的密集命中合成一个缓冲区。
        if (_nextHit < _hits.Length && _hits[_nextHit].TimeSamples < scheduleEnd)
        {
            var groups = new Dictionary<int, float[]>();
            var scheduled = false;
            while (_nextHit < _hits.Length && _hits[_nextHit].TimeSamples < scheduleEnd)
            {
                var hit = _hits[_nextHit++];
                if (hit.TimeSamples < _position || hit.SoundIndex < 0 || hit.SoundIndex >= _sounds.Length)
                {
                    continue;
                }

                var bus = hit.LineIndex >= 0 && hit.LineIndex < _buses.Length ? _buses[hit.LineIndex] : null;
                if (bus is not null && (bus.Muted || anySolo && !bus.Solo))
                {
                    continue;
                }

                var lineGain = bus?.Volume ?? 1;
                var gain = lineGain * _hitVolume * Math.Max(1, hit.Count);
                if (gain <= 0)
                {
                    continue;
                }

                var sound = _sounds[hit.SoundIndex];
                if (!groups.TryGetValue(hit.SoundIndex, out var mixed))
                {
                    mixed = new float[(scheduleFrames + sound.Frames) * 2];
                    groups[hit.SoundIndex] = mixed;
                }

                var startFrame = (int)Math.Round((hit.TimeSamples - _position) / _rate);
                startFrame = Math.Clamp(startFrame, 0, scheduleFrames);
                for (var frame = 0; frame < sound.Frames; frame++)
                {
                    var destination = (startFrame + frame) * 2;
                    if (destination + 1 >= mixed.Length)
                    {
                        break;
                    }

                    var source = frame * 2;
                    mixed[destination] += sound.Samples[source] * gain;
                    mixed[destination + 1] += sound.Samples[source + 1] * gain;
                }

                scheduled = true;
            }

            if (scheduled)
            {
                foreach (var mixed in groups.Values)
                {
                    var source = new PcmSampleProvider(new PcmAudioBuffer(mixed));
                    _active.Add(source);
                    _mixer.AddMixerInput(source);
                }
            }
        }

        var read = _mixer.Read(buffer, offset, count);
        RemoveFinished();
        _position += (long)Math.Round(frames * _rate);
        return read;
    }

    private void RemoveFinished()
    {
        for (var index = _active.Count - 1; index >= 0; index--)
        {
            if (!_active[index].Finished)
            {
                continue;
            }

            _mixer.RemoveMixerInput(_active[index]);
            _active.RemoveAt(index);
        }
    }

    private static int LowerBound(IReadOnlyList<ScheduledHit> hits, long sample)
    {
        var low = 0;
        var high = hits.Count;
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

using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace PhiEdit.App.Services.Audio;

/// <summary>把预解码 PCM 作为 ISampleProvider 播放，支持起始延迟与增益。</summary>
public sealed class PcmSampleProvider : ISampleProvider
{
    private readonly PcmAudioBuffer _buffer;

    public PcmSampleProvider(PcmAudioBuffer buffer, int startFrame = 0, float gain = 1, int delayFrames = 0)
    {
        _buffer = buffer;
        Position = Math.Clamp(startFrame, 0, buffer.Frames);
        Gain = gain;
        DelayFrames = Math.Max(0, delayFrames);
    }

    public int Position { get; private set; }

    public int DelayFrames { get; private set; }

    public float Gain { get; set; }

    public bool Finished => DelayFrames <= 0 && Position >= _buffer.Frames;

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

            if (Position >= _buffer.Frames)
            {
                continue;
            }

            var index = Position * 2;
            buffer[offset + frame * 2] = _buffer.Samples[index] * Gain;
            buffer[offset + frame * 2 + 1] = _buffer.Samples[index + 1] * Gain;
            Position++;
        }

        return count;
    }
}

/// <summary>
/// 用 NAudio 标准 MixingSampleProvider 混合背景与命中音，避免手工样本复制引入杂音；
/// 命中音按谱面绝对采样点加入混音器，延迟量化到输出缓冲区以内。
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
            : new PcmSampleProvider(music, (int)Math.Clamp(musicOffsetSamples, 0, music.Frames), _musicVolume);
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
            _musicSource = new PcmSampleProvider(_music, offset, _musicVolume);
            _mixer.AddMixerInput(_musicSource);
        }
    }

    public void SetRate(double rate) => _rate = rate is > 0 and < 100 ? rate : 1;

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
        // 参考 large-chart-hitsounds.mjs：同一时刻同一音效已聚合为一条带 count 的条目，
        // 这里直接按 count 作为权重创建单个音源，而不是为每根线各开一个声部。
        RemoveFinished();

        var frames = count / 2;
        var end = _position + (long)Math.Round(frames * _rate);
        // 参考 hitsounds.mjs 的 0.1s/0.3s 前瞻调度：提前把命中音加入混音器，
        // 主线程或音频回调偶发抖动时也不会错过密集音符。
        var scheduleEnd = end + (long)Math.Round(ScheduleAheadSeconds * PcmAudioBuffer.SampleRate * _rate);
        var anySolo = false;
        foreach (var bus in _buses)
        {
            if (bus.Solo)
            {
                anySolo = true;
                break;
            }
        }

        while (_nextHit < _hits.Length && _hits[_nextHit].TimeSamples < scheduleEnd)
        {
            var hit = _hits[_nextHit++];
            if (hit.TimeSamples < _position || hit.SoundIndex < 0 || hit.SoundIndex >= _sounds.Length)
            {
                continue;
            }

            var bus = hit.LineIndex >= 0 && hit.LineIndex < _buses.Length ? _buses[hit.LineIndex] : null;
            var gain = bus is null
                ? _hitVolume
                : bus.Muted || anySolo && !bus.Solo ? 0 : bus.Volume * _hitVolume;
            if (gain <= 0)
            {
                continue;
            }

            var delay = (int)Math.Max(0, Math.Round((hit.TimeSamples - _position) / _rate));
            var source = new PcmSampleProvider(_sounds[hit.SoundIndex], 0, gain * hit.Count, delay);
            _active.Add(source);
            _mixer.AddMixerInput(source);
        }

        var read = _mixer.Read(buffer, offset, count);
        RemoveFinished();
        _position = end;
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

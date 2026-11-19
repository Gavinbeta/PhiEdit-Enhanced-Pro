using PhiEdit.Core.Beats;

namespace PhiEdit.Core.Playback;

/// <summary>
/// 播放时钟。以拍为基准推进，支持变速点与倍速。
/// </summary>
public sealed class PlaybackClock
{
    private readonly System.Diagnostics.Stopwatch _watch = new();

    private double _anchorBeat;

    private double _anchorSeconds;

    public TempoMap Tempo { get; set; } = TempoMap.Default;

    public bool IsPlaying { get; private set; }

    /// <summary>播放倍速。</summary>
    public double Rate { get; set; } = 1.0;

    /// <summary>当前播放位置（拍）。</summary>
    public double CurrentBeat { get; private set; }

    /// <summary>当前播放位置（秒）。</summary>
    public double CurrentSeconds => Tempo.SecondsAt(CurrentBeat);

    public void Play()
    {
        if (IsPlaying)
        {
            return;
        }

        IsPlaying = true;
        _anchorBeat = CurrentBeat;
        _anchorSeconds = _watch.Elapsed.TotalSeconds;
    }

    public void Pause()
    {
        if (!IsPlaying)
        {
            return;
        }

        Advance(0);
        IsPlaying = false;
        _watch.Stop();
    }

    public void Toggle()
    {
        if (IsPlaying)
        {
            Pause();
        }
        else
        {
            Play();
        }
    }

    /// <summary>跳转到指定拍。</summary>
    public void SeekToBeat(double beat)
    {
        CurrentBeat = Math.Max(0, beat);
        _anchorBeat = CurrentBeat;
        _anchorSeconds = _watch.Elapsed.TotalSeconds;
    }

    /// <summary>按真实时间推进播放位置。</summary>
    public void Advance(double deltaSeconds)
    {
        if (!IsPlaying)
        {
            return;
        }

        var now = _watch.Elapsed.TotalSeconds;
        var elapsed = (now - _anchorSeconds) * Rate;
        _anchorSeconds = now;
        CurrentBeat = Math.Max(0, Tempo.BeatAt(Tempo.SecondsAt(_anchorBeat) + elapsed));
    }

    public void Reset()
    {
        IsPlaying = false;
        _watch.Stop();
        _watch.Reset();
        CurrentBeat = 0;
        _anchorBeat = 0;
        _anchorSeconds = 0;
    }
}

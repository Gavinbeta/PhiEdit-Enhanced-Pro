namespace PhiEdit.Core.Preview;

/// <summary>
/// 预览时间源。后续接入音频时可实现为 AudioContext/媒体播放器包装；当前使用高精度时钟占位。
/// 时间单位为秒，与参考源码 audio.time 的语义一致。
/// </summary>
public interface IPreviewTimeSource
{
    double CurrentSeconds { get; }

    bool IsPlaying { get; }

    double Rate { get; set; }

    void Play();

    void Pause();

    void Seek(double seconds);
}

/// <summary>
/// 不依赖音频的预览时钟：使用 Stopwatch 锚点推进，避免逐帧累加误差。
/// </summary>
public sealed class PreviewClock : IPreviewTimeSource
{
    private readonly System.Diagnostics.Stopwatch _watch = System.Diagnostics.Stopwatch.StartNew();
    private double _anchorSeconds;
    private double _position;

    public double CurrentSeconds => IsPlaying
        ? _position + (_watch.Elapsed.TotalSeconds - _anchorSeconds) * Rate
        : _position;

    public bool IsPlaying { get; private set; }

    public double Rate { get; set; } = 1;

    public void Play()
    {
        if (IsPlaying)
        {
            return;
        }

        _position = CurrentSeconds;
        _anchorSeconds = _watch.Elapsed.TotalSeconds;
        IsPlaying = true;
    }

    public void Pause()
    {
        if (!IsPlaying)
        {
            return;
        }

        _position = CurrentSeconds;
        IsPlaying = false;
    }

    public void Seek(double seconds)
    {
        _position = Math.Max(0, double.IsFinite(seconds) ? seconds : 0);
        _anchorSeconds = _watch.Elapsed.TotalSeconds;
    }
}

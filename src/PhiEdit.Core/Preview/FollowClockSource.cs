using PhiEdit.Core.Playback;

namespace PhiEdit.Core.Preview;

/// <summary>
/// 预览时间源适配器：只读地跟随编辑器的播放时钟。
/// 嵌入编辑区的预览画布使用它来保证预览播放头与编辑区完全一致；
/// Play/Pause/Seek 转发到编辑时钟，Rate 读取编辑时钟倍速。
/// </summary>
public sealed class FollowClockSource : IPreviewTimeSource
{
    private readonly PlaybackClock _clock;

    public FollowClockSource(PlaybackClock clock) => _clock = clock;

    public double CurrentSeconds => _clock.CurrentSeconds;

    public bool IsPlaying => _clock.IsPlaying;

    public double Rate
    {
        get => _clock.Rate;
        set => _clock.Rate = value;
    }

    public void Play() => _clock.Play();

    public void Pause() => _clock.Pause();

    public void Seek(double seconds) => _clock.SeekToBeat(_clock.Tempo.BeatAt(seconds));
}

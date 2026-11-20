namespace PhiEdit.Core.Preview;

/// <summary>
/// 音频播放接口预留。实时预览层的播放时间未来由音频时钟驱动，
/// 当前独立窗口使用 <see cref="IPreviewTimeSource"/> 的高精度占位时钟。
/// </summary>
public interface IPreviewAudioPlayer : IPreviewTimeSource, IDisposable
{
    /// <summary>音频总时长（秒）。</summary>
    double Duration { get; }

    /// <summary>载入音频文件。路径由谱面 META.song 与资源目录解析。</summary>
    Task LoadAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>音频是否就绪。</summary>
    bool IsLoaded { get; }
}

namespace PhiEdit.Core.Layout;

/// <summary>
/// 编辑视口：负责拍与像素、舞台坐标与像素之间的双向映射。
///
/// 时间轴自下而上流逝（与原版 RPE 一致）：拍号越大越靠上，越靠下拍号越小。
/// 判定线（播放头）固定在画布底部，锚点拍 <see cref="AnchorBeat"/> 即判定线所在处的拍。
/// 音符区与判定线区共用同一个实例，因此两侧始终同步滚动。
/// </summary>
public sealed class EditViewport
{
    /// <summary>判定线（播放头）所在处的拍。</summary>
    public double AnchorBeat { get; set; }

    /// <summary>每拍像素高度（纵向缩放）。</summary>
    public double PixelsPerBeat { get; set; } = 240;

    /// <summary>视图高度（像素），由界面在尺寸变化时更新。</summary>
    public double ViewportHeight { get; set; } = 600;

    /// <summary>判定线距画布底部的距离，保留一条可以看到「已过」音符的窄带。</summary>
    public double PlayheadInset { get; set; } = 26;

    /// <summary>判定线（播放头）所在的屏幕纵坐标。</summary>
    public double PlayheadY => Math.Max(0, ViewportHeight - PlayheadInset);

    /// <summary>可见拍数。</summary>
    public double VisibleBeats => PlayheadY / Math.Max(1e-6, PixelsPerBeat);

    /// <summary>画布顶端对应的拍（最大的可见拍）。</summary>
    public double TopBeat => AnchorBeat + VisibleBeats;

    /// <summary>画布底端对应的拍（最小的可见拍，可能为负，仅位于判定线以下的窄带内）。</summary>
    public double BottomBeat => AnchorBeat - PlayheadInset / Math.Max(1e-6, PixelsPerBeat);

    /// <summary>拍 → 屏幕纵坐标。拍越大越靠上。</summary>
    public double YAtBeat(double beat) => PlayheadY - (beat - AnchorBeat) * PixelsPerBeat;

    /// <summary>屏幕纵坐标 → 拍。</summary>
    public double BeatAtY(double y) => AnchorBeat + (PlayheadY - y) / Math.Max(1e-6, PixelsPerBeat);

    /// <summary>
    /// 按像素增量滚动。增量为正表示内容向下移动（看到更晚的拍）。
    /// 拍号不会出现负值：锚点始终不小于 0。
    /// </summary>
    public void ScrollByPixels(double delta)
    {
        AnchorBeat = Math.Max(0, AnchorBeat + delta / Math.Max(1e-6, PixelsPerBeat));
    }

    /// <summary>把判定线定位到指定拍。</summary>
    public void ScrollToBeat(double beat) => AnchorBeat = Math.Max(0, beat);

    /// <summary>以指定拍为锚点缩放纵向视图。</summary>
    public void ZoomAt(double pixelY, double factor, double minimum = 24, double maximum = 2400)
    {
        var anchor = BeatAtY(pixelY);
        PixelsPerBeat = Math.Clamp(PixelsPerBeat * factor, minimum, maximum);
        AnchorBeat = Math.Max(0, anchor);
    }

    /// <summary>舞台 X 坐标 → 音符区像素横坐标。</summary>
    public double StageXToPixel(double stageX) =>
        (stageX + VerticalGrid.StageHalfWidth) * NoteWidth / VerticalGrid.StageWidth;

    /// <summary>音符区像素横坐标 → 舞台 X 坐标。</summary>
    public double PixelToStageX(double pixelX) =>
        pixelX * VerticalGrid.StageWidth / Math.Max(1e-6, NoteWidth) - VerticalGrid.StageHalfWidth;

    /// <summary>音符区宽度，用于把舞台 X 换算成像素。</summary>
    public double NoteWidth { get; set; } = 480;
}

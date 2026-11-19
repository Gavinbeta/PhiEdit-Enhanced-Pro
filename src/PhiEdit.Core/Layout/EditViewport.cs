namespace PhiEdit.Core.Layout;

/// <summary>
/// 编辑视口：负责拍与像素、舞台坐标与像素之间的双向映射。
/// 音符区与判定线区共享同一时间轴，因此共用同一个实例。
/// </summary>
public sealed class EditViewport
{
    /// <summary>视图顶端对应的拍。</summary>
    public double TopBeat { get; set; }

    /// <summary>每拍像素高度（纵向缩放）。</summary>
    public double PixelsPerBeat { get; set; } = 240;

    /// <summary>视图高度（像素），由界面在尺寸变化时更新。</summary>
    public double ViewportHeight { get; set; } = 600;

    /// <summary>横向缩放：音符区中每舞台单位对应的像素数。</summary>
    public double HorizontalScale { get; set; } = 1;

    /// <summary>音符区宽度，用于把舞台 X 换算成像素。</summary>
    public double NoteWidth { get; set; } = 480;

    public double VisibleBeats => ViewportHeight / Math.Max(1e-6, PixelsPerBeat);

    public double BottomBeat => TopBeat + VisibleBeats;

    public double BeatAtY(double y) => TopBeat + y / Math.Max(1e-6, PixelsPerBeat);

    public double YAtBeat(double beat) => (beat - TopBeat) * PixelsPerBeat;

    /// <summary>把视图顶端对齐到指定拍。</summary>
    public void ScrollToBeat(double beat) => TopBeat = beat;

    /// <summary>按像素增量滚动。</summary>
    public void ScrollByPixels(double delta) => TopBeat += delta / Math.Max(1e-6, PixelsPerBeat);

    /// <summary>以指定拍为锚点缩放纵向视图。</summary>
    public void ZoomAt(double pixelY, double factor, double minimum = 24, double maximum = 2400)
    {
        var anchor = BeatAtY(pixelY);
        PixelsPerBeat = Math.Clamp(PixelsPerBeat * factor, minimum, maximum);
        TopBeat = anchor - pixelY / Math.Max(1e-6, PixelsPerBeat);
    }

    /// <summary>舞台 X 坐标 → 音符区像素横坐标。</summary>
    public double StageXToPixel(double stageX) =>
        (stageX + VerticalGrid.StageHalfWidth) * NoteWidth / VerticalGrid.StageWidth;

    /// <summary>音符区像素横坐标 → 舞台 X 坐标。</summary>
    public double PixelToStageX(double pixelX) =>
        pixelX * VerticalGrid.StageWidth / Math.Max(1e-6, NoteWidth) - VerticalGrid.StageHalfWidth;

    /// <summary>让指定拍始终处于视图内（跟随播放头）。</summary>
    public void FollowBeat(double beat, double anchorRatio = 0.25)
    {
        if (beat < TopBeat || beat > BottomBeat - VisibleBeats * anchorRatio)
        {
            TopBeat = beat - VisibleBeats * anchorRatio;
        }
    }
}

namespace PhiEdit.Core.Layout;

/// <summary>
/// 编辑视口：拍与像素、舞台坐标与像素之间的双向映射。
///
/// 纵向位置由唯一真源 <see cref="Position"/> 决定——它表示判定线（播放头）所在的拍。
/// 音符区与判定线区共用同一个视口实例，两侧都只读取该值，
/// 因此任何改变纵向位置的操作（滚轮、缩放、进度条、播放）都会同时作用于两侧。
///
/// 时间轴自下而上流逝（与原版 RPE 一致）：拍号越大越靠上，越靠下拍号越小。
/// </summary>
public sealed class EditViewport
{
    private double _position;

    /// <summary>
    /// 纵向位置的唯一真源：判定线（播放头）所在的拍。
    /// 恒不小于 0，保证拍号不会出现负数。
    /// </summary>
    public double Position
    {
        get => _position;
        private set => _position = Math.Max(0, value);
    }

    /// <summary>每拍像素高度（纵向缩放）。</summary>
    public double PixelsPerBeat { get; set; } = 240;

    /// <summary>视图高度（像素），由界面在尺寸变化时更新。</summary>
    public double ViewportHeight { get; set; } = 600;

    /// <summary>判定线距画布底部的距离，保留一条可以看到「已过」音符的窄带。</summary>
    public double PlayheadInset { get; set; } = 88;

    /// <summary>判定线（播放头）所在的屏幕纵坐标。</summary>
    public double PlayheadY => Math.Max(0, ViewportHeight - PlayheadInset);

    /// <summary>可见拍数。</summary>
    public double VisibleBeats => PlayheadY / Math.Max(1e-6, PixelsPerBeat);

    /// <summary>画布顶端对应的拍（最大的可见拍）。</summary>
    public double TopBeat => Position + VisibleBeats;

    /// <summary>画布底端对应的拍（最小的可见拍，可能为负，仅位于判定线以下的窄带内）。</summary>
    public double BottomBeat => Position - PlayheadInset / Math.Max(1e-6, PixelsPerBeat);

    /// <summary>拍 → 屏幕纵坐标。拍越大越靠上。</summary>
    public double YAtBeat(double beat) => PlayheadY - (beat - Position) * PixelsPerBeat;

    /// <summary>屏幕纵坐标 → 拍。</summary>
    public double BeatAtY(double y) => Position + (PlayheadY - y) / Math.Max(1e-6, PixelsPerBeat);

    /// <summary>
    /// 纵向位置或缩放发生变化时触发。两侧编辑区与底栏读数都监听此事件，
    /// 保证任何入口的改动都会立即、同步地反映到整个界面。
    /// </summary>
    public event EventHandler? ViewChanged;

    /// <summary>设置纵向位置（判定线所在的拍）。</summary>
    public void SetPosition(double beat)
    {
        var value = Math.Max(0, beat);
        if (Math.Abs(value - _position) < 1e-9)
        {
            return;
        }

        _position = value;
        RaiseViewChanged();
    }

    /// <summary>按像素增量滚动。增量为正表示内容向下移动（看到更晚的拍）。</summary>
    public void ScrollByPixels(double delta) =>
        SetPosition(_position + delta / Math.Max(1e-6, PixelsPerBeat));

    /// <summary>按拍增量滚动。增量为负表示回到更早的拍。</summary>
    public void ScrollByBeats(double beats) => SetPosition(_position + beats);

    /// <summary>把判定线定位到指定拍。</summary>
    public void ScrollToBeat(double beat) => SetPosition(beat);

    /// <summary>以指定像素为锚点缩放纵向视图，锚点处的拍保持不动。</summary>
    public void ZoomAt(double pixelY, double factor, double minimum = 24, double maximum = 2400)
    {
        var scale = Math.Clamp(PixelsPerBeat * factor, minimum, maximum);
        var anchorBeat = BeatAtY(pixelY);
        PixelsPerBeat = scale;
        Position = anchorBeat - (PlayheadY - pixelY) / scale;
        RaiseViewChanged();
    }

    /// <summary>舞台 X 坐标 → 音符区像素横坐标。</summary>
    public double StageXToPixel(double stageX) =>
        (stageX + VerticalGrid.StageHalfWidth) * NoteWidth / VerticalGrid.StageWidth;

    /// <summary>音符区像素横坐标 → 舞台 X 坐标。</summary>
    public double PixelToStageX(double pixelX) =>
        pixelX * VerticalGrid.StageWidth / Math.Max(1e-6, NoteWidth) - VerticalGrid.StageHalfWidth;

    /// <summary>音符区宽度，用于把舞台 X 换算成像素。</summary>
    public double NoteWidth { get; set; } = 480;

    private void RaiseViewChanged() => ViewChanged?.Invoke(this, EventArgs.Empty);
}

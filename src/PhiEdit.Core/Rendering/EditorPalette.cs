using System.Drawing;

namespace PhiEdit.Core.Rendering;

/// <summary>
/// 编辑器配色。所有颜色集中在此，便于主题化与扩展。
/// 约定：
/// 整拍线为红色并加粗，横线与竖线为黄色细线。
/// </summary>
public sealed class EditorPalette
{
    public Color Surface { get; set; } = Color.FromArgb(255, 28, 28, 30);

    public Color SurfaceAlt { get; set; } = Color.FromArgb(255, 36, 36, 38);

    public Color PanelHeader { get; set; } = Color.FromArgb(255, 48, 48, 48);

    /// <summary>整拍线颜色（红）。</summary>
    public Color BeatLine { get; set; } = Color.FromArgb(255, 255, 59, 48);

    /// <summary>整拍线宽度（像素）。</summary>
    public float BeatLineWidth { get; set; } = 7f;

    /// <summary>网格距编辑区左右边界的留白（像素）。</summary>
    public double GridInsetX { get; set; } = 28;

    /// <summary>网格距编辑区顶部的留白（像素）。</summary>
    public double GridInsetTop { get; set; } = 16;

    /// <summary>网格与判定线之间的留白（像素）。</summary>
    public double GridInsetBottom { get; set; } = 16;

    /// <summary>细分线颜色（绿）。</summary>
    public Color Subdivision { get; set; } = Color.FromArgb(255, 74, 168, 78);

    /// <summary>次级细分线颜色（绿，更淡）。</summary>
    public Color MinorSubdivision { get; set; } = Color.FromArgb(255, 74, 168, 78);

    /// <summary>细分线宽度（像素）。</summary>
    public float SubdivisionWidth { get; set; } = 3.5f;

    /// <summary>次级细分线宽度（像素，与细分线一致）。</summary>
    public float MinorSubdivisionWidth { get; set; } = 3.5f;

    /// <summary>细分线不透明度。</summary>
    public byte SubdivisionAlpha { get; set; } = 150;

    /// <summary>次级细分线不透明度。</summary>
    public byte MinorSubdivisionAlpha { get; set; } = 95;

    /// <summary>框选区域的填充色。</summary>
    public Color MarqueeFill { get; set; } = Color.FromArgb(255, 129, 191, 255);

    /// <summary>框选区域的边框色。</summary>
    public Color MarqueeStroke { get; set; } = Color.FromArgb(255, 129, 191, 255);

    /// <summary>竖线颜色（黄）。</summary>
    public Color VerticalLine { get; set; } = Color.FromArgb(255, 255, 224, 64);

    /// <summary>竖线不透明度。</summary>
    public byte VerticalAlpha { get; set; } = 150;

    /// <summary>竖线宽度（像素）。</summary>
    public float VerticalWidth { get; set; } = 3f;

    /// <summary>居中竖线宽度（像素），竖线数量为奇数时使用。</summary>
    public float VerticalCenterWidth { get; set; } = 4.5f;

    /// <summary>拍号文字颜色（红色，与整拍线一致）。</summary>
    public Color BeatLabel { get; set; } = Color.FromArgb(255, 255, 76, 64);

    /// <summary>判定线颜色。</summary>
    public Color Judgement { get; set; } = Color.FromArgb(255, 255, 255, 255);

    /// <summary>判定线不透明度。</summary>
    public byte JudgementAlpha { get; set; } = 153;

    /// <summary>判定线宽度（像素）。</summary>
    public float JudgementWidth { get; set; } = 5f;

    public Color NoteTap { get; set; } = Color.FromArgb(255, 138, 203, 255);

    public Color NoteHold { get; set; } = Color.FromArgb(255, 138, 203, 255);

    public Color NoteFlick { get; set; } = Color.FromArgb(255, 245, 150, 172);

    public Color NoteDrag { get; set; } = Color.FromArgb(255, 241, 206, 118);

    /// <summary>选中描边颜色（原版 RPE 为绿色）。</summary>
    public Color Selection { get; set; } = Color.FromArgb(255, 0, 230, 60);

    /// <summary>选中时覆盖在物体上的半透明绿色。</summary>
    public Color SelectionFill { get; set; } = Color.FromArgb(255, 0, 230, 60);

    /// <summary>事件条内缓动曲线颜色（原版为橙色细线）。</summary>
    public Color EventCurve { get; set; } = Color.FromArgb(255, 255, 140, 24);

    public Color EventFill { get; set; } = Color.FromArgb(255, 229, 141, 36);

    public Color EventStroke { get; set; } = Color.FromArgb(255, 255, 163, 52);

    public Color EventFillSelected { get; set; } = Color.FromArgb(255, 0, 230, 60);

    public Color EventStrokeSelected { get; set; } = Color.FromArgb(255, 0, 255, 80);

    public Color EventFillHooked { get; set; } = Color.FromArgb(255, 98, 216, 242);

    public Color EventStrokeHooked { get; set; } = Color.FromArgb(255, 184, 242, 255);

    /// <summary>事件栏之间的竖向分隔线（暗黄色，保持与网格竖线一致的观感）。</summary>
    public Color EventColumnSeparator { get; set; } = Color.FromArgb(255, 176, 160, 48);

    public Color EventHeaderBackground { get; set; } = Color.FromArgb(255, 48, 48, 48);

    public Color EventHeaderText { get; set; } = Color.FromArgb(255, 245, 245, 245);

    public Color EventValueText { get; set; } = Color.FromArgb(255, 246, 229, 206);

    public Color EventValueTop { get; set; } = Color.FromArgb(255, 128, 255, 160);

    public Color EventValueBottom { get; set; } = Color.FromArgb(255, 255, 224, 128);

    /// <summary>按音符种类取色。</summary>
    public Color NoteColor(Charts.NoteType type) => type switch
    {
        Charts.NoteType.Tap => NoteTap,
        Charts.NoteType.Hold => NoteHold,
        Charts.NoteType.Flick => NoteFlick,
        Charts.NoteType.Drag => NoteDrag,
        _ => NoteTap,
    };

    /// <summary>与 alpha 组合。</summary>
    public static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);
}

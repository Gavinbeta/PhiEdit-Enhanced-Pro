using Microsoft.Graphics.Canvas.Text;
using System.Drawing;
using Windows.UI;
using Windows.UI.Text;
using Color = System.Drawing.Color;

namespace PhiEdit.App.Controls;

/// <summary>Win2D 绘制辅助方法。</summary>
public static class CanvasExtensions
{
    private static readonly Dictionary<(float Size, bool Center, bool Right, bool Bold), CanvasTextFormat> Formats = new();

    public static Windows.UI.Color ToWinColor(this Color color) =>
        Windows.UI.Color.FromArgb(color.A, color.R, color.G, color.B);

    public static CanvasTextFormat TextFormat(float size, bool center = false, bool right = false, bool bold = false)
    {
        var key = (size, center, right, bold);
        if (Formats.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var format = new CanvasTextFormat
        {
            FontFamily = "Microsoft YaHei UI",
            FontSize = size,
            FontWeight = bold
                ? new Windows.UI.Text.FontWeight { Weight = 700 }
                : new Windows.UI.Text.FontWeight { Weight = 400 },
            HorizontalAlignment = right ? CanvasHorizontalAlignment.Right : center ? CanvasHorizontalAlignment.Center : CanvasHorizontalAlignment.Left,
            VerticalAlignment = CanvasVerticalAlignment.Top,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };

        Formats[key] = format;
        return format;
    }
}

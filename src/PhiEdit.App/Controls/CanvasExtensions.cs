using Microsoft.Graphics.Canvas.Text;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using Windows.UI;
using Windows.UI.Text;
using Color = System.Drawing.Color;

namespace PhiEdit.App.Controls;

/// <summary>Win2D 绘制辅助方法。</summary>
public static class CanvasExtensions
{
    /// <summary>实时预览与渲染专用的数字/英文字体，主界面不使用。</summary>
    public const string PreviewFontFamily = "Assets/Fonts/cmdysj.ttf#cmdysj";

    private static readonly Dictionary<(float Size, bool Center, bool Right, bool Bold, string Family), CanvasTextFormat> Formats = new();
    private static bool _previewFontAdded;

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int AddFontResourceExW(string file, uint flags, IntPtr reserved);

    /// <summary>以私有方式注册随程序分发的 TTF，避免修改系统字体目录。</summary>
    public static void EnsurePreviewFont()
    {
        if (_previewFontAdded)
        {
            return;
        }

        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "cmdysj.ttf");
        if (File.Exists(path))
        {
            AddFontResourceExW(path, 0x10, IntPtr.Zero);
        }

        _previewFontAdded = true;
    }

    public static Windows.UI.Color ToWinColor(this Color color) =>
        Windows.UI.Color.FromArgb(color.A, color.R, color.G, color.B);

    public static CanvasTextFormat TextFormat(float size, bool center = false, bool right = false,
        bool bold = false, string? family = null)
    {
        var fontFamily = family ?? "Microsoft YaHei UI";
        if (fontFamily == PreviewFontFamily)
        {
            EnsurePreviewFont();
        }

        var key = (size, center, right, bold, fontFamily);
        if (Formats.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var format = new CanvasTextFormat
        {
            FontFamily = fontFamily,
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

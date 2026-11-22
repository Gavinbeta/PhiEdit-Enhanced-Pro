using Microsoft.Graphics.Canvas;
using System.IO;
using System.Numerics;
using Windows.Foundation;
using Windows.UI;
using SD = System.Drawing;
using SD2D = System.Drawing.Drawing2D;
using SDImaging = System.Drawing.Imaging;
using SDText = System.Drawing.Text;

namespace PhiEdit.App.Controls;

/// <summary>
/// 实时预览专用 ASCII 字体图集。使用 GDI+ 私有字体加载 TTF 并预渲染字形，
/// Win2D 只负责贴图绘制，避免系统字体集合与 DirectWrite COM 私有字体兼容问题。
/// 主界面不调用这个类。
/// </summary>
public sealed class PreviewFontAtlas
{
    private const int FirstChar = 32;
    private const int CharCount = 95;
    private const int CellWidth = 64;
    private const int CellHeight = 96;
    private const int FontSize = 68;

    private CanvasBitmap? _white;
    private CanvasBitmap? _green;
    private CanvasBitmap? _gold;
    private static byte[]? _whitePng;
    private static byte[]? _greenPng;
    private static byte[]? _goldPng;
    private static readonly float[] Advances = new float[CharCount];
    private static bool _advancesReady;

    private readonly record struct TextKey(string Text, int Size, uint Color,
        bool Center, bool Right, bool Middle, bool Bottom);

    private readonly record struct CachedText(CanvasRenderTarget Bitmap, float OffsetX, float OffsetY);

    private readonly Dictionary<TextKey, CachedText> _textCache = new();
    private ICanvasResourceCreator? _textCreator;

    public bool IsReady => _white is not null && _green is not null && _gold is not null;

    public async Task LoadAsync(ICanvasResourceCreator creator)
    {
        if (IsReady)
        {
            return;
        }

        _whitePng ??= BuildAtlas(SD.Color.White);
        _greenPng ??= BuildAtlas(SD.Color.FromArgb(255, 0, 200, 0));
        _goldPng ??= BuildAtlas(SD.Color.FromArgb(255, 241, 216, 148));
        _white = await Load(creator, _whitePng);
        _green = await Load(creator, _greenPng);
        _gold = await Load(creator, _goldPng);
    }

    public void Reset()
    {
        ClearTextCache();
        _white?.Dispose();
        _green?.Dispose();
        _gold?.Dispose();
        _white = null;
        _green = null;
        _gold = null;
    }

    private void ClearTextCache()
    {
        foreach (var cached in _textCache.Values)
        {
            cached.Bitmap.Dispose();
        }

        _textCache.Clear();
        _textCreator = null;
    }

    public float Measure(string text, float size)
    {
        var scale = size / FontSize;
        var width = 0f;
        foreach (var character in text)
        {
            var index = character - FirstChar;
            width += index >= 0 && index < CharCount && _advancesReady
                ? Advances[index] * scale
                : CellWidth * scale;
        }

        return width;
    }

    public void Draw(CanvasDrawingSession drawing, string text, Vector2 origin, float size,
        Color color, bool center = false, bool right = false, bool middle = false, bool bottom = false)
    {
        if (!IsReady || string.IsNullOrEmpty(text))
        {
            drawing.DrawText(text, origin, color, CanvasExtensions.TextFormat(size));
            return;
        }

        var argb = (uint)((color.A << 24) | (color.R << 16) | (color.G << 8) | color.B);
        var atlas = argb == 0xFFFFFFFF ? _white
            : argb == 0xFF00C800 ? _green
            : argb == 0xFFF1D894 ? _gold
            : null;
        if (atlas is null)
        {
            drawing.DrawText(text, origin, color, CanvasExtensions.TextFormat(size));
            return;
        }

        if (!ReferenceEquals(_textCreator, drawing))
        {
            ClearTextCache();
            _textCreator = drawing;
        }

        var key = new TextKey(text, Math.Max(1, (int)Math.Round(size)), argb,
            center, right, middle, bottom);
        if (!_textCache.TryGetValue(key, out var cached))
        {
            if (_textCache.Count >= 512)
            {
                foreach (var old in _textCache.Values)
                {
                    old.Bitmap.Dispose();
                }

                _textCache.Clear();
            }

            cached = RenderCached(drawing, atlas, text, size, center, right, middle, bottom);
            _textCache[key] = cached;
        }

        var bitmap = cached.Bitmap;
        drawing.DrawImage(bitmap,
            new Rect(origin.X + cached.OffsetX, origin.Y + cached.OffsetY,
                bitmap.Size.Width, bitmap.Size.Height),
            new Rect(0, 0, bitmap.Size.Width, bitmap.Size.Height), 1);
    }

    private CachedText RenderCached(ICanvasResourceCreator creator, CanvasBitmap atlas, string text,
        float size, bool center, bool right, bool middle, bool bottom)
    {
        const int padding = 4;
        var scale = size / FontSize;
        var cellWidth = CellWidth * scale;
        var cellHeight = CellHeight * scale;
        var width = Measure(text, size);
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(width + padding * 2));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(cellHeight + padding * 2));
        var target = new CanvasRenderTarget(creator, pixelWidth, pixelHeight, 96f);

        using (var session = target.CreateDrawingSession())
        {
            var penX = (float)padding;
            var glyphY = (float)padding;
            for (var index = 0; index < text.Length; index++)
            {
                var glyphIndex = text[index] - FirstChar;
                if (glyphIndex < 0 || glyphIndex >= CharCount)
                {
                    continue;
                }

                var advance = (_advancesReady ? Advances[glyphIndex] : CellWidth) * scale;
                session.DrawImage(atlas,
                    new Rect(penX + (advance - cellWidth) / 2, glyphY, cellWidth, cellHeight),
                    new Rect(glyphIndex * CellWidth, 0, CellWidth, CellHeight), 1);
                penX += (float)advance;
            }
        }

        var offsetX = center ? -width / 2 - padding : right ? -width - padding : -padding;
        var offsetY = middle ? -cellHeight / 2 - padding : bottom ? -cellHeight - padding : -padding;
        return new CachedText(target, (float)offsetX, (float)offsetY);
    }

    private static async Task<CanvasBitmap> Load(ICanvasResourceCreator creator, byte[] png)
    {
        using var stream = new MemoryStream(png, writable: false);
        using var random = stream.AsRandomAccessStream();
        return await CanvasBitmap.LoadAsync(creator, random);
    }

    private static byte[] BuildAtlas(SD.Color color)
    {
        using var bitmap = new SD.Bitmap(FirstChar + CharCount * CellWidth, CellHeight,
            SDImaging.PixelFormat.Format32bppArgb);
        using var graphics = SD.Graphics.FromImage(bitmap);
        graphics.Clear(SD.Color.Transparent);
        graphics.TextRenderingHint = SDText.TextRenderingHint.AntiAliasGridFit;
        graphics.InterpolationMode = SD2D.InterpolationMode.HighQualityBicubic;

        var fontPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "cmdysj.ttf");
        using var collection = new SDText.PrivateFontCollection();
        collection.AddFontFile(fontPath);
        using var font = new SD.Font(collection.Families[0], FontSize, SD.FontStyle.Regular, SD.GraphicsUnit.Pixel);
        using var brush = new SD.SolidBrush(color);
        using var format = new SD.StringFormat
        {
            Alignment = SD.StringAlignment.Center,
            LineAlignment = SD.StringAlignment.Center,
            FormatFlags = SD.StringFormatFlags.NoWrap,
        };

        for (var index = 0; index < CharCount; index++)
        {
            var character = (char)(FirstChar + index);
            if (!_advancesReady)
            {
                var measured = graphics.MeasureString(character.ToString(), font,
                    SD.PointF.Empty, SD.StringFormat.GenericTypographic).Width;
                Advances[index] = Math.Max(1, measured);
            }

            graphics.DrawString(character.ToString(), font, brush,
                new SD.RectangleF(index * CellWidth, 0, CellWidth, CellHeight), format);
        }

        _advancesReady = true;

        using var stream = new MemoryStream();
        bitmap.Save(stream, SDImaging.ImageFormat.Png);
        return stream.ToArray();
    }
}

using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using System.Runtime.CompilerServices;
using Windows.Foundation;

namespace PhiEdit.App.Services;

/// <summary>
/// 按贴图、裁剪区域与颜色缓存染色结果，避免每帧为每个音符新建 ColorMatrixEffect。
/// 对应 rpe-next skin.mjs 的 tints 缓存策略。
/// </summary>
public sealed class TintedBitmapCache
{
    private const int MaxEntries = 192;
    private const long MaxBytes = 64L * 1024 * 1024;

    private readonly Dictionary<Key, CanvasBitmap> _cache = new();
    private readonly Queue<Key> _order = new();
    private long _bytes;

    private readonly record struct Key(int Bitmap, int X, int Y, int Width, int Height, int R, int G, int B);

    public CanvasBitmap Get(ICanvasResourceCreator creator, CanvasBitmap source, Rect sourceRect,
        IReadOnlyList<double> color)
    {
        var rgb = new[]
        {
            (int)Math.Clamp(color[0], 0, 255),
            (int)Math.Clamp(color[1], 0, 255),
            (int)Math.Clamp(color[2], 0, 255),
        };
        var key = new Key(
            RuntimeHelpers.GetHashCode(source),
            (int)sourceRect.X, (int)sourceRect.Y,
            (int)sourceRect.Width, (int)sourceRect.Height,
            rgb[0], rgb[1], rgb[2]);

        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var width = Math.Max(1, (int)Math.Ceiling(sourceRect.Width));
        var height = Math.Max(1, (int)Math.Ceiling(sourceRect.Height));
        var target = new CanvasRenderTarget(creator, width, height, source.Dpi);
        var matrix = new Matrix5x4
        {
            M11 = rgb[0] / 255f,
            M22 = rgb[1] / 255f,
            M33 = rgb[2] / 255f,
            M44 = 1,
        };
        using (var drawing = target.CreateDrawingSession())
        using (var effect = new ColorMatrixEffect { Source = source, ColorMatrix = matrix })
        {
            drawing.DrawImage(effect, new Rect(0, 0, width, height), sourceRect, 1);
        }

        var bytes = (long)width * height * 4;
        while (_cache.Count > 0 && (_cache.Count >= MaxEntries || _bytes + bytes > MaxBytes))
        {
            var oldest = _order.Dequeue();
            if (_cache.Remove(oldest, out var removed))
            {
                _bytes -= (long)removed.Size.Width * (long)removed.Size.Height * 4;
                removed.Dispose();
            }
        }

        _cache[key] = target;
        _order.Enqueue(key);
        _bytes += bytes;
        return target;
    }

    public void Reset()
    {
        foreach (var bitmap in _cache.Values)
        {
            bitmap.Dispose();
        }
        _cache.Clear();
        _order.Clear();
        _bytes = 0;
    }
}

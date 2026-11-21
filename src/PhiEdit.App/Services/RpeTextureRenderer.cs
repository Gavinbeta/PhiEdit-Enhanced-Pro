using Microsoft.Graphics.Canvas;
using System.IO;

namespace PhiEdit.App.Services;

/// <summary>
/// 原版默认 RPE 贴图加载器。实时预览层按参考源码的 Tap2/Hold/Flick2/Drag2/img-N 规则绘制。
/// 每个 CanvasControl 设备都需要独立实例，避免跨设备复用 CanvasBitmap。
/// </summary>
public sealed class RpeTextureRenderer
{
    private readonly Dictionary<string, CanvasBitmap> _bitmaps = new(StringComparer.OrdinalIgnoreCase);

    public static readonly string[] Names = BuildNames();

    private static string[] BuildNames() =>
    [
        "line.png", "Arrow2.png", "Pause.png", "Tap2.png", "Tap2HL.png", "Hold.png", "Hold3.png", "HoldEnd.png", "HoldHL.png",
        "HoldHead.png", "HoldHeadHL.png", "Flick2.png", "Flick2HL.png", "Drag2.png", "DragHL.png",
        .. Enumerable.Range(1, 31).Select(index => $"img-{index}.png"),
    ];

    public bool IsReady => _bitmaps.Count > 0;

    public CanvasBitmap? this[string name] => _bitmaps.TryGetValue(name, out var bitmap) ? bitmap : null;

    public async Task LoadAsync(ICanvasResourceCreator creator)
    {
        if (IsReady)
        {
            return;
        }

        var root = Path.Combine(AppContext.BaseDirectory, "Assets", "RpeTextures");
        foreach (var name in Names)
        {
            var path = Path.Combine(root, name);
            if (!File.Exists(path))
            {
                continue;
            }

            using var stream = File.OpenRead(path);
            using var random = stream.AsRandomAccessStream();
            _bitmaps[name] = await CanvasBitmap.LoadAsync(creator, random);
        }
    }

    public void Reset()
    {
        foreach (var bitmap in _bitmaps.Values)
        {
            bitmap.Dispose();
        }

        _bitmaps.Clear();
    }
}

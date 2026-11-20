using Microsoft.Graphics.Canvas;
using System.IO;
using PhiEdit.Core.Skins;

namespace PhiEdit.App.Services;

/// <summary>
/// 资源包贴图渲染器：把资源包中的 PNG 加载成 Win2D 位图，供音符绘制使用。
/// </summary>
public sealed class NoteSkinRenderer
{
    private readonly Dictionary<string, CanvasBitmap> _bitmaps = new(StringComparer.OrdinalIgnoreCase);

    private string? _skinId;

    /// <summary>当前渲染的资源包。</summary>
    public NoteSkin? Skin { get; private set; }

    /// <summary>是否已经加载过当前资源包的贴图。</summary>
    public bool IsReady => _skinId is not null && _skinId == Skin?.Id && _bitmaps.Count > 0;

    /// <summary>按名称取贴图。</summary>
    public CanvasBitmap? this[string name] => _bitmaps.TryGetValue(name, out var bitmap) ? bitmap : null;

    /// <summary>加载资源包的全部贴图（异步，避免阻塞界面）。</summary>
    public async Task<bool> ApplyAsync(ICanvasResourceCreator creator, NoteSkin skin)
    {
        var loaded = new Dictionary<string, CanvasBitmap>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in NoteSkin.RequiredImages)
        {
            var bytes = skin.GetFile(name);
            if (bytes is null)
            {
                continue;
            }

            using var stream = new MemoryStream(bytes, writable: false);
            using var random = stream.AsRandomAccessStream();
            loaded[name] = await CanvasBitmap.LoadAsync(creator, random);
        }

        _bitmaps.Clear();
        foreach (var pair in loaded)
        {
            _bitmaps[pair.Key] = pair.Value;
        }

        Skin = skin;
        _skinId = skin.Id;
        return true;
    }

    /// <summary>释放已加载的位图。</summary>
    public void Reset()
    {
        foreach (var bitmap in _bitmaps.Values)
        {
            bitmap.Dispose();
        }

        _bitmaps.Clear();
        _skinId = null;
        Skin = null;
    }
}

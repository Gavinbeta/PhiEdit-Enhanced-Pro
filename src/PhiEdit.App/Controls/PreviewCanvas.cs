using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhiEdit.App.Services;
using PhiEdit.Core.Beats;
using PhiEdit.Core.Charts;
using PhiEdit.Core.Editing;
using PhiEdit.Core.IO;
using PhiEdit.Core.Preview;
using PhiEdit.Core.Skins;
using System.Globalization;
using System.Numerics;
using Windows.Foundation;
using EasingMath = PhiEdit.Core.Easing.Easing;

namespace PhiEdit.App.Controls;

/// <summary>
/// 实时预览画布。始终渲染整份谱面的全部判定线，不受当前编辑线影响；
/// 画布本身不处理任何指针输入，仅作为只读预览层。
/// </summary>
public sealed class PreviewCanvas : UserControl
{
    private const double LogicalNoteSize = 175;
    private const double DefaultLineWidth = 4000;
    private const double StageWidth = 1350;
    private const double StageHeight = 900;
    private const double HitFrameSeconds = 0.015;
    private const int HitFrameCount = 31;

    private readonly CanvasControl _canvas = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
    };

    private readonly SceneRuntime _scene = new();
    private readonly RpeTextureRenderer _textures = new();
    private readonly NoteSkinRenderer _packRenderer = new();
    private readonly TintedBitmapCache _tintCache = new();
    private readonly PreviewFontAtlas _fontAtlas = new();
    private readonly Dictionary<(string Text, int Size), CanvasTextLayout> _textLayouts = new();
    private readonly Dictionary<(string Text, int Size, bool Bold), CanvasTextLayout> _guideLayouts = new();
    private readonly Dictionary<string, CanvasBitmap> _assetTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loadingTextures = new(StringComparer.OrdinalIgnoreCase);
    private EditorSession? _session;
    private IPreviewTimeSource? _clock;
    private ICanvasResourceCreator? _creator;
    private string? _loadedPackId;
    private bool _packLoading;
    private ChartDocument? _tempoDocument;
    private TempoMap? _tempoMap;
    private string? _tempoSignature;
    private CanvasBitmap? _background;
    private GaussianBlurEffect? _backgroundEffect;
    private string? _backgroundKey;
    private ChartDocument? _backgroundDocument;
    private ChartAssetStore? _backgroundAssets;
    private bool _backgroundLoading;
    private bool _backgroundResolved;
    private long _backgroundGeneration;
    private double _backgroundBrightness = 1;
    private long _packGeneration;
    private long _assetGeneration;
    private ChartDocument? _hudDocument;
    private readonly List<double> _hudTimes = new();
    private double _hudDuration;
    private bool _loading;

    public PreviewCanvas()
    {
        Content = _canvas;
        _canvas.Draw += OnDraw;
        _canvas.CreateResources += (sender, _) => EnsureTexturesLoaded(sender);
        _canvas.SizeChanged += (_, _) => Invalidate();
    }

    /// <summary>背景亮度，0 为纯黑，1 为原图亮度；对应参考 preview.backgroundAlpha。</summary>
    public double BackgroundBrightness
    {
        get => _backgroundBrightness;
        set
        {
            var clamped = Math.Clamp(value, 0, 1);
            if (Math.Abs(_backgroundBrightness - clamped) < 1e-6)
            {
                return;
            }

            _backgroundBrightness = clamped;
            Invalidate();
        }
    }

    public EditorSession? Session
    {
        get => _session;
        set
        {
            if (_session is not null)
            {
                _session.Changed -= OnSessionChanged;
                _session.Skins.Changed -= OnSkinChanged;
            }

            _session = value;
            if (_session is not null)
            {
                _session.Changed += OnSessionChanged;
                _session.Skins.Changed += OnSkinChanged;
            }

            _backgroundKey = null;
            _backgroundResolved = false;
            _backgroundDocument = null;
            _backgroundAssets = null;
            InvalidateGenerations();
            foreach (var bitmap in _assetTextures.Values)
            {
                bitmap.Dispose();
            }
            _assetTextures.Clear();
            _scene.MarkDirty();
            Invalidate();
        }
    }

    public IPreviewTimeSource? Clock
    {
        get => _clock;
        set
        {
            _clock = value;
            Invalidate();
        }
    }

    private void InvalidateGenerations()
    {
        _backgroundGeneration++;
        _packGeneration++;
        _assetGeneration++;
        _backgroundLoading = false;
        _packLoading = false;
        _loadingTextures.Clear();
    }

    public void Invalidate() => _canvas.Invalidate();

    /// <summary>窗口关闭时释放该窗口设备上的位图与效果。</summary>
    public void ReleaseResources()
    {
        _backgroundEffect?.Dispose();
        _background?.Dispose();
        _backgroundEffect = null;
        _background = null;
        _textures.Reset();
        _packRenderer.Reset();
        _tintCache.Reset();
        _fontAtlas.Reset();
        foreach (var layout in _textLayouts.Values)
        {
            layout.Dispose();
        }
        _textLayouts.Clear();
        foreach (var bitmap in _assetTextures.Values)
        {
            bitmap.Dispose();
        }
        _assetTextures.Clear();
    }

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        _scene.MarkDirty();
        _backgroundDocument = null;
        _backgroundResolved = false;
        _backgroundGeneration++;
        _backgroundLoading = false;
        _assetGeneration++;
        _loadingTextures.Clear();
        Invalidate();
    }

    private void OnSkinChanged(object? sender, EventArgs e)
    {
        _packRenderer.Reset();
        _loadedPackId = null;
        _packGeneration++;
        _packLoading = false;
        Invalidate();
    }

    private void EnsurePackLoaded(ICanvasResourceCreator creator)
    {
        var skin = _session?.Skins.Active;
        if (_packLoading || (skin is null && _loadedPackId is null) ||
            (skin is not null && skin.Id == _loadedPackId && _packRenderer.IsReady))
        {
            return;
        }

        if (skin is null)
        {
            _packRenderer.Reset();
            _loadedPackId = null;
            return;
        }

        _packLoading = true;
        var token = ++_packGeneration;
        _ = LoadPackAsync(creator, skin, token);
    }

    private async Task LoadPackAsync(ICanvasResourceCreator creator, NoteSkin skin, long token)
    {
        try
        {
            _packRenderer.Reset();
            await _packRenderer.ApplyAsync(creator, skin);
            if (token != _packGeneration || !ReferenceEquals(skin, _session?.Skins.Active))
            {
                _packRenderer.Reset();
                _loadedPackId = null;
                return;
            }

            _loadedPackId = skin.Id;
            Invalidate();
        }
        catch
        {
            _loadedPackId = null;
        }
        finally
        {
            if (token == _packGeneration)
            {
                _packLoading = false;
            }
        }
    }

    private void EnsureTexturesLoaded(ICanvasResourceCreator creator)
    {
        if (_loading || _textures.IsReady)
        {
            return;
        }

        _loading = true;
        _ = LoadAsync(creator);
    }

    private async Task LoadAsync(ICanvasResourceCreator creator)
    {
        try
        {
            await _textures.LoadAsync(creator);
            await _fontAtlas.LoadAsync(creator);
            Invalidate();
        }
        finally
        {
            _loading = false;
        }
    }

    private void EnsureBackgroundLoaded(ICanvasResourceCreator creator, string? name)
    {
        if (string.IsNullOrEmpty(name) || _backgroundLoading)
        {
            return;
        }

        if (name == _backgroundKey && _background is not null)
        {
            return;
        }

        var bytes = _session?.Assets.Resolve(name);
        if (bytes is null)
        {
            return;
        }

        _backgroundKey = name;
        _backgroundLoading = true;
        var token = ++_backgroundGeneration;
        _ = LoadBackgroundAsync(creator, bytes, name, token);
    }

    private async Task LoadBackgroundAsync(ICanvasResourceCreator creator, byte[] bytes, string name, long token)
    {
        CanvasBitmap? loaded = null;
        GaussianBlurEffect? effect = null;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var random = stream.AsRandomAccessStream();
            loaded = await CanvasBitmap.LoadAsync(creator, random);
            effect = new GaussianBlurEffect { Source = loaded, BlurAmount = 10.5f };
            if (token != _backgroundGeneration)
            {
                effect.Dispose();
                loaded.Dispose();
                return;
            }

            _background?.Dispose();
            _backgroundEffect?.Dispose();
            _background = loaded;
            _backgroundEffect = effect;
            Invalidate();
        }
        catch
        {
            effect?.Dispose();
            loaded?.Dispose();
            if (token == _backgroundGeneration)
            {
                _background = null;
                _backgroundEffect = null;
                _backgroundKey = null;
            }
        }
        finally
        {
            if (token == _backgroundGeneration)
            {
                _backgroundLoading = false;
            }
        }
    }

    private void EnsureLineTexture(ICanvasResourceCreator creator, string? name)
    {
        if (string.IsNullOrEmpty(name) || _assetTextures.ContainsKey(name) || !_loadingTextures.Add(name))
        {
            return;
        }

        var bytes = _session?.Assets.Resolve(name);
        if (bytes is null)
        {
            _loadingTextures.Remove(name);
            return;
        }

        var token = _assetGeneration;
        _ = LoadLineTextureAsync(creator, name, bytes, token);
    }

    private async Task LoadLineTextureAsync(ICanvasResourceCreator creator, string name, byte[] bytes, long token)
    {
        CanvasBitmap? loaded = null;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var random = stream.AsRandomAccessStream();
            loaded = await CanvasBitmap.LoadAsync(creator, random);
            if (token != _assetGeneration)
            {
                loaded.Dispose();
                return;
            }

            _assetTextures[name] = loaded;
            Invalidate();
        }
        catch
        {
            loaded?.Dispose();
        }
        finally
        {
            _loadingTextures.Remove(name);
        }
    }

    private TempoMap GetTempo(ChartDocument document)
    {
        var signature = string.Join(';', document.BpmList.Select(point =>
            $"{point.StartBeat.Value:R}:{point.Bpm:R}"));
        if (_tempoMap is not null && ReferenceEquals(_tempoDocument, document) && _tempoSignature == signature)
        {
            return _tempoMap;
        }

        _tempoDocument = document;
        _tempoSignature = signature;
        _tempoMap = document.BuildTempoMap();
        _scene.MarkDirty();
        return _tempoMap;
    }

    private void PrepareHud(ChartDocument document, TempoMap tempo)
    {
        if (ReferenceEquals(_hudDocument, document) && _hudTimes.Count > 0)
        {
            return;
        }

        _hudDocument = document;
        _hudTimes.Clear();
        var lastBeat = 0.0;
        var offset = document.OffsetMilliseconds / 1000.0;
        foreach (var line in document.Lines)
        {
            foreach (var note in line.Notes)
            {
                if (note.IsFake)
                {
                    continue;
                }

                _hudTimes.Add(tempo.SecondsAt(note.StartBeat.Value, line.BpmFactor) + offset);
                lastBeat = Math.Max(lastBeat, note.EndBeat.Value);
            }
        }

        _hudTimes.Sort();
        _hudDuration = document.SongDuration > 0
            ? document.SongDuration
            : tempo.SecondsAt(lastBeat + 4);
    }

    /// <summary>
    /// 参考 game-ui.mjs：暂停、进度、连击、分数、曲名与难度。
    /// 这里只按用户截图布局，不使用皮肤差异。
    /// </summary>
    private void DrawGameUi(CanvasDrawingSession drawing, float scale, double seconds,
        PreviewViewport viewport)
    {
        PrepareHud(_session!.Document, GetTempo(_session.Document));
        var white = Windows.UI.Color.FromArgb(255, 255, 255, 255);
        var total = _hudTimes.Count;
        var combo = LowerBound(_hudTimes, seconds);
        var score = total == 0 ? 0 : 1_000_000.0 * combo / total;

        var originX = viewport.Left + viewport.Width / 2;
        var originY = viewport.Top + viewport.Height / 2;
        float X(double logical) => (float)(originX + logical * scale);
        float Y(double logical) => (float)(originY - logical * scale);

        var pause = _textures["Pause.png"];
        if (pause is not null)
        {
            var size = 42 * scale;
            drawing.DrawImage(pause,
                new Rect(X(-634.68), Y(419.04) - size, size, size),
                new Rect(0, 0, pause.Size.Width, pause.Size.Height), 1);
        }

        var progress = Math.Clamp((seconds + _session.Document.OffsetMilliseconds / 1000.0) /
            Math.Max(0.001, _hudDuration), 0, 1);
        drawing.FillRectangle(new Rect(viewport.Left, viewport.Top,
            viewport.Width * progress, 8 * scale), white);

        if (combo >= 3)
        {
            DrawUiText(drawing, combo.ToString(CultureInfo.InvariantCulture),
                X(0.072), Y(405), 67 * scale, white, center: true, middle: true);
            DrawUiText(drawing, "combo",
                X(0.072), Y(360), 35 * scale, white, center: true, middle: true);
        }

        DrawUiText(drawing, ((long)Math.Round(score)).ToString("D7", CultureInfo.InvariantCulture),
            X(641.16), Y(427.46), 49 * scale, white, right: true);
        DrawUiText(drawing, _session.Document.SongName,
            X(-636.6), Y(-427.5), 35 * scale, white);
        DrawUiText(drawing, _session.Document.Difficulty,
            X(641.16), Y(-427.5), 33 * scale, white, right: true);
    }

    private void DrawUiText(CanvasDrawingSession drawing, string text, float x, float y,
        float size, Windows.UI.Color color, bool center = false, bool right = false, bool middle = false) =>
        _fontAtlas.Draw(drawing, text, new Vector2(x, y), size, color, center, right, middle: middle);

    private static int LowerBound(IReadOnlyList<double> values, double value)
    {
        var low = 0;
        var high = values.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (values[middle] < value)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        _creator = sender;
        var width = (float)Math.Max(1, sender.ActualWidth);
        var height = (float)Math.Max(1, sender.ActualHeight);
        var drawing = args.DrawingSession;
        drawing.Clear(Windows.UI.Color.FromArgb(255, 0, 0, 0));

        EnsureTexturesLoaded(sender);
        EnsurePackLoaded(sender);
        var viewport = PreviewViewport.Create(width, height);
        if (!ReferenceEquals(_backgroundDocument, session.Document) || !ReferenceEquals(_backgroundAssets, session.Assets))
        {
            _backgroundDocument = session.Document;
            _backgroundAssets = session.Assets;
            _backgroundKey = null;
            _backgroundResolved = false;
        }

        if (!_backgroundResolved)
        {
            _backgroundKey = session.Assets.FindBackground(session.Document.Background);
            _backgroundResolved = true;
        }

        var backgroundName = _backgroundKey;
        EnsureBackgroundLoaded(sender, backgroundName);
        using (drawing.CreateLayer(1, new Rect(viewport.Left, viewport.Top, viewport.Width, viewport.Height)))
        {
            DrawBackground(drawing, width, height, viewport.Scale);
        }

        if (_background is null)
        {
            drawing.FillRectangle(
                new Rect(viewport.Left, viewport.Top, viewport.Width, viewport.Height),
                Windows.UI.Color.FromArgb(255, 0, 0, 0));
        }
        drawing.DrawRectangle(
            new Rect(viewport.Left, viewport.Top, viewport.Width, viewport.Height),
            Windows.UI.Color.FromArgb(255, 84, 96, 116));

        var tempo = GetTempo(session.Document);
        _scene.Compile(session.Document, tempo);
        var seconds = _clock?.CurrentSeconds ?? tempo.SecondsAt(session.Viewport.Position);
        var states = _scene.Sample(seconds, PreviewViewport.PositionScale());
        var scale = (float)viewport.Scale;
        var originX = (float)(viewport.Left + viewport.Width / 2);
        var originY = (float)(viewport.Top + viewport.Height / 2);

        var visibleCache = new Dictionary<int, IReadOnlyList<NoteEntry>>();
        var aspectRatio = viewport.Height > 0 ? viewport.Width / viewport.Height : 1.5;
        var positionScale = PreviewViewport.PositionScale(aspectRatio);
        var visibleDivisor = Math.Max(1, viewport.Width / Math.Max(1, scale) / StageWidth);
        for (var index = 0; index < states.Count; index++)
        {
            var lineDistance = Math.Sqrt(states[index].X * states[index].X + states[index].Y * states[index].Y);
            var radius = (1200 * visibleDivisor + lineDistance) / Math.Max(0.01, Math.Abs(positionScale.Y));
            visibleCache[index] = _scene.Lines[index].VisibleNotes(seconds, states[index], radius);
        }

        using (drawing.CreateLayer(1, new Rect(viewport.Left, viewport.Top, viewport.Width, viewport.Height)))
        {
            var selectedLine = _session?.CurrentLineIndex ?? -1;
            DrawPass(drawing, sender, PassKind.Line, states, originX, originY, scale, viewport, selectedLine, seconds, visibleCache);
            DrawPass(drawing, sender, PassKind.Hold, states, originX, originY, scale, viewport, selectedLine, seconds, visibleCache);
            DrawPass(drawing, sender, PassKind.Note, states, originX, originY, scale, viewport, selectedLine, seconds, visibleCache);
            DrawHits(drawing, states, originX, originY, scale, seconds, viewport);
            DrawGameUi(drawing, scale, seconds, viewport);
            DrawGuides(drawing, sender, states, scale, selectedLine, viewport);
        }

        var format = CanvasExtensions.TextFormat(12);
        drawing.DrawText("实时预览 · 整份谱面", new Vector2(10, 8),
            Windows.UI.Color.FromArgb(190, 210, 220, 235), format);
    }

    private void DrawBackground(CanvasDrawingSession drawing, float width, float height, double scale)
    {
        if (_background is null)
        {
            return;
        }

        var factor = Math.Max(StageWidth / _background.Size.Width, StageHeight / _background.Size.Height) * scale;
        var drawWidth = _background.Size.Width * factor;
        var drawHeight = _background.Size.Height * factor;
        var destination = new Rect((width - drawWidth) / 2, (height - drawHeight) / 2, drawWidth, drawHeight);
        var source = new Rect(0, 0, _background.Size.Width, _background.Size.Height);
        var opacity = (float)Math.Clamp(_backgroundBrightness, 0, 1);
        if (opacity <= 0)
        {
            return;
        }

        // 直接绘制原图，避免 Win2D 效果链在部分设备上返回空结果导致背景不可见。
        drawing.DrawImage(_background, destination, source, opacity);
    }

    private enum PassKind { Line, Hold, Note }

    private void DrawPass(CanvasDrawingSession drawing, ICanvasResourceCreator creator, PassKind pass,
        IReadOnlyList<LineState> states, float originX, float originY, float scale, PreviewViewport viewport,
        int selectedLine, double seconds, Dictionary<int, IReadOnlyList<NoteEntry>> visibleCache)
    {
        var aspectRatio = viewport.Height > 0 ? viewport.Width / viewport.Height : 1.5;
        var positionScale = PreviewViewport.PositionScale(aspectRatio);
        var visibleDivisor = Math.Max(1, viewport.Width / Math.Max(1, scale) / StageWidth);
        foreach (var index in _scene.Order)
        {
            if (index < 0 || index >= states.Count)
            {
                continue;
            }

            var runtime = _scene.Lines[index];
            var state = states[index];
            // 参考 preview.mjs drawPass：只有线 pass 会因线透明度/缩放/attachUI 跳过；
            // 音符与 Hold pass 仍然要绘制，否则线淡出时音符会一起消失。
            if (pass == PassKind.Line &&
                (runtime.Line.AttachUI || state.Alpha <= 0 || state.ScaleX == 0 || state.ScaleY == 0))
            {
                continue;
            }

            if (pass != PassKind.Line &&
                (!visibleCache.TryGetValue(index, out var cached) || cached.Count == 0))
            {
                continue;
            }

            var centerX = originX + (float)(state.X * scale);
            var centerY = originY - (float)(state.Y * scale);
            var oldTransform = drawing.Transform;
            var angleRad = state.Rotation * Math.PI / 180;
            var cosine = Math.Cos(angleRad);
            var sine = Math.Sin(angleRad);
            drawing.Transform = Matrix3x2.CreateRotation((float)angleRad, new Vector2(centerX, centerY));

            if (pass == PassKind.Line)
            {
                var halfWidth = Math.Abs(DefaultLineWidth * state.ScaleX * scale) / 2 + 32;
                var halfHeight = Math.Abs(24 * state.ScaleY * scale) + 32;
                if (centerX + halfWidth < viewport.Left || centerX - halfWidth > viewport.Left + viewport.Width ||
                    centerY + halfHeight < viewport.Top || centerY - halfHeight > viewport.Top + viewport.Height)
                {
                    drawing.Transform = oldTransform;
                    continue;
                }

                DrawLine(drawing, creator, runtime, state, centerX, centerY, scale, index == selectedLine);
            }
            else
            {
                if (!visibleCache.TryGetValue(index, out var entries))
                {
                    // 参考 preview.mjs drawScene：远处判定线的音符可以被画布中心之外很长的距离，
                    // 固定 1600 会先把它从 VisibleNotes 过滤掉。按线中心到舞台中心的距离扩大半径。
                    var lineDistance = Math.Sqrt(state.X * state.X + state.Y * state.Y);
                    var radius = (1600 * visibleDivisor + lineDistance) /
                                 Math.Max(0.01, Math.Abs(positionScale.Y));
                    entries = runtime.VisibleNotes(seconds, state, radius);
                    visibleCache[index] = entries;
                }

                foreach (var entry in entries)
                {
                    if ((entry.Note.Type == NoteType.Hold) != (pass == PassKind.Hold))
                    {
                        continue;
                    }

                    var position = runtime.NoteState(entry, state, seconds);
                    if (position.Alpha <= 0 || Math.Abs(position.Size) < 1e-6)
                    {
                        continue;
                    }

                    var localX = position.X;
                    var localY = position.Y;
                    var localTail = position.Tail;
                    // 参考 editor-display.mjs previewNotePosition：无父线的音符局部距离随预览比例缩放。
                    if (runtime.Line.Father < 0 && (positionScale.X != 1 || positionScale.Y != 1))
                    {
                        localX *= positionScale.X;
                        localY *= positionScale.Y;
                        localTail *= positionScale.Y;
                    }

                    var noteX = centerX + (float)(localX * scale);
                    var headY = centerY - (float)(localY * scale);
                    var tailY = centerY - (float)(localTail * scale);
                    var noteWidth = (float)(LogicalNoteSize * scale * position.Size);
                    var heightWidth = (float)(LogicalNoteSize * scale * position.SizeY);
                    var highlight = _scene.Simultaneous.Contains(entry.Note);
                    var tint = entry.Note.Tint ?? entry.Note.Color;
                    var pack = _packRenderer.IsReady ? _packRenderer.Skin : null;
                    var textureScale = highlight && pack is not null ? pack.HighlightScale : 1;
                    noteWidth *= (float)textureScale;
                    heightWidth *= (float)textureScale;

                    // 参考 preview.mjs drawScene 的 cullNotes：只丢弃完全在视口外的音符，
                    // 大型谱面中可显著减少贴图与斜切变换次数。
                    var worldX = state.X + localX * cosine + localY * sine;
                    var worldY = -state.Y + localX * sine - localY * cosine;
                    var tailWorldX = state.X + localX * cosine + localTail * sine;
                    var tailWorldY = -state.Y + localX * sine - localTail * cosine;
                    var screenX = originX + (float)(worldX * scale);
                    var screenY = originY + (float)(worldY * scale);
                    var tailX = originX + (float)(tailWorldX * scale);
                    var tailYWorld = originY + (float)(tailWorldY * scale);
                    var padding = Math.Max(40, Math.Abs(noteWidth) + Math.Abs(heightWidth) * 0.6f);
                    if (Math.Max(screenX, tailX) + padding < viewport.Left ||
                        Math.Min(screenX, tailX) - padding > viewport.Left + viewport.Width ||
                        Math.Max(screenY, tailYWorld) + padding < viewport.Top ||
                        Math.Min(screenY, tailYWorld) - padding > viewport.Top + viewport.Height)
                    {
                        continue;
                    }

                    var noteTransform = drawing.Transform;
                    if (Math.Abs(position.Skew) > 1e-6)
                    {
                        // 与 canvas 的 translate(center) + transform(skew) 等价：
                        // 先在音符局部坐标做斜切，再平移到判定线上的音符中心。
                        var skew = new Matrix3x2(1, 0, (float)Math.Tan(position.Skew * Math.PI / 180), 1, 0, 0);
                        drawing.Transform = skew *
                                            Matrix3x2.CreateTranslation(noteX, headY) * noteTransform;
                    }

                    DrawNote(drawing, entry.Note, noteX, headY, tailY, noteWidth, heightWidth,
                        highlight, position.ShowHead, tint, (float)Math.Clamp(position.Alpha, 0, 1));
                    drawing.Transform = noteTransform;
                }
            }

            drawing.Transform = oldTransform;
        }
    }

    private void DrawLine(CanvasDrawingSession drawing, ICanvasResourceCreator creator,
        LineRuntime runtime, LineState state, float centerX, float centerY, float scale, bool selected)
    {
        var line = runtime.Line;
        var isDefault = string.IsNullOrEmpty(line.Texture) || line.Texture == "line.png";
        var alpha = (float)Math.Clamp(state.Alpha / 255, 0, 1);
        var pack = _packRenderer.IsReady ? _packRenderer.Skin : null;
        double[] lineColor = state.Color;

        // 参考 preview.mjs drawScene：正在编辑的判定线染成绿色，优先于资源包 perfectColor。
        if (selected)
        {
            lineColor = new double[] { 0, 200, 0 };
        }
        else
        {
            // 实时预览中未编辑的判定线统一纯白；资源包只保留其透明度信息。
            lineColor = new double[] { 255, 255, 255 };
            if (pack is not null && isDefault && !line.AttachUI &&
                !runtime.HasTextEvents && !runtime.HasColorEvents)
            {
                alpha *= (float)((pack.Info.ColorPerfect >> 24) & 0xFF) / 255f;
            }
        }

        var tint = Rgb(lineColor);
        if (alpha <= 0)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(state.Text))
        {
            DrawLineText(drawing, state.Text, line, centerX, centerY, scale, tint, alpha);
            return;
        }

        double width;
        double height;
        CanvasBitmap? bitmap;
        if (isDefault)
        {
            var thickness = runtime.HasScaleYEvents ? 5 : 7.5;
            width = DefaultLineWidth * scale;
            height = thickness * scale;
            bitmap = _textures["line.png"];
        }
        else
        {
            var textureName = line.Texture!;
            EnsureLineTexture(creator, textureName);
            bitmap = _assetTextures.TryGetValue(textureName, out var asset) ? asset : null;
            width = bitmap?.Size.Width ?? 512;
            height = bitmap?.Size.Height ?? 16;
        }

        width *= state.ScaleX;
        height *= state.ScaleY;
        var leftEdge = centerX - (float)(width * line.AnchorX);
        var top = centerY - (float)(height * (1 - line.AnchorY));
        var destination = new Rect(leftEdge, top, Math.Max(1, Math.Abs((float)width)), Math.Max(1, Math.Abs((float)height)));

        if (bitmap is null)
        {
            drawing.FillRectangle(destination, WithAlpha(tint, alpha));
            return;
        }

        DrawTinted(drawing, bitmap, destination, new Rect(0, 0, bitmap.Size.Width, bitmap.Size.Height), lineColor, alpha);
    }

    private void DrawLineText(CanvasDrawingSession drawing, string text, JudgeLine line,
        float centerX, float centerY, float scale, Windows.UI.Color color, float alpha)
    {
        if (_creator is null)
        {
            return;
        }

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var size = Math.Max(1, (int)Math.Round(52 * scale));
        if (normalized.All(character => character <= 0x7F) && !normalized.Contains('\n'))
        {
            var glyphWidth = _fontAtlas.Measure(normalized, size);
            var glyphLeft = (float)(centerX - glyphWidth * line.AnchorX);
            var glyphTop = (float)(centerY - size * (1 - line.AnchorY));
            _fontAtlas.Draw(drawing, normalized,
                new Vector2(glyphLeft, glyphTop), size, WithAlpha(color, alpha));
            return;
        }

        var key = (normalized, size);
        if (!_textLayouts.TryGetValue(key, out var layout))
        {
            if (_textLayouts.Count >= 128)
            {
                foreach (var old in _textLayouts.Values)
                {
                    old.Dispose();
                }
                _textLayouts.Clear();
            }

            layout = new CanvasTextLayout(_creator, normalized,
                CanvasExtensions.TextFormat(size, family: CanvasExtensions.PreviewFontFamily), 10000, 10000);
            _textLayouts[key] = layout;
        }

        var bounds = layout.LayoutBounds;
        var left = centerX - (float)(bounds.Width * line.AnchorX);
        var top = centerY - (float)(bounds.Height * (1 - line.AnchorY));
        drawing.DrawTextLayout(layout, new Vector2(left, top), WithAlpha(color, alpha));
    }

    private void DrawNote(CanvasDrawingSession drawing, Note note, float centerX, float headY, float tailY,
        float width, float heightWidth, bool highlight, bool showHead, double[]? tint, float alpha)
    {
        if (note.Type == NoteType.Hold)
        {
            DrawHold(drawing, note, centerX, headY, tailY, width, heightWidth, highlight, showHead, tint, alpha);
            return;
        }

        var pack = _packRenderer.IsReady ? _packRenderer.Skin : null;
        if (pack is not null)
        {
            var packName = note.Type switch
            {
                NoteType.Flick => highlight ? "flick_mh.png" : "flick.png",
                NoteType.Drag => highlight ? "drag_mh.png" : "drag.png",
                _ => highlight ? "click_mh.png" : "click.png",
            };
            var packed = _packRenderer[packName];
            if (packed is not null)
            {
                var packedHeight = Math.Max(5, heightWidth * packed.Size.Height / Math.Max(1, packed.Size.Width));
                DrawTinted(drawing, packed,
                    new Rect(centerX - width / 2, headY - packedHeight / 2, width, packedHeight),
                    new Rect(0, 0, packed.Size.Width, packed.Size.Height), tint, alpha);
                return;
            }
        }

        var name = note.Type switch
        {
            NoteType.Flick => highlight ? "Flick2HL.png" : "Flick2.png",
            NoteType.Drag => highlight ? "DragHL.png" : "Drag2.png",
            _ => highlight ? "Tap2HL.png" : "Tap2.png",
        };
        var bitmap = _textures[name];
        if (bitmap is null)
        {
            drawing.FillRectangle(new Rect(centerX - width / 2, headY - 2, width, 4), Rgb(tint ?? DefaultNoteColor(note.Type)));
            return;
        }

        var height = Math.Max(5, heightWidth * bitmap.Size.Height / Math.Max(1, bitmap.Size.Width));
        var destination = new Rect(centerX - width / 2, headY - height / 2, width, height);
        DrawTinted(drawing, bitmap, destination, new Rect(0, 0, bitmap.Size.Width, bitmap.Size.Height), tint, alpha);
    }

    private void DrawHold(CanvasDrawingSession drawing, Note note, float centerX, float headY, float tailY,
        float width, float heightWidth, bool highlight, bool showHead, double[]? tint, float alpha)
    {
        var pack = _packRenderer.IsReady ? _packRenderer.Skin : null;
        if (pack is not null)
        {
            DrawPackHold(drawing, pack, centerX, headY, tailY, width, heightWidth, highlight, showHead, note.IsUp, tint, alpha);
            return;
        }

        var body = _textures[highlight ? "HoldHL.png" : "Hold3.png"] ?? _textures["Hold.png"];
        var end = _textures["HoldEnd.png"];
        var head = _textures[highlight ? "HoldHeadHL.png" : "HoldHead.png"];
        if (body is null)
        {
            return;
        }

        var zeroDirection = note.IsUp ? 1 : -1;
        var direction = Math.Abs(tailY - headY) < 0.01 ? zeroDirection : tailY < headY ? 1 : -1;
        var length = Math.Abs(tailY - headY);
        var unit = width / Math.Max(1, body.Size.Width);
        var unitY = heightWidth / Math.Max(1, body.Size.Width);

        if (length > 0)
        {
            var bodyTop = headY - direction * length;
            DrawTinted(drawing, body,
                new Rect(centerX - width / 2, Math.Min(bodyTop, headY), width, Math.Max(1, length)),
                new Rect(0, 0, body.Size.Width, body.Size.Height), tint, alpha);
        }

        if (end is not null)
        {
            var endWidth = end.Size.Width * unit;
            var endHeight = end.Size.Height * unitY;
            var endTop = headY - direction * (length + endHeight);
            DrawTinted(drawing, end,
                new Rect(centerX - endWidth / 2, Math.Min(endTop, endTop + endHeight), endWidth, endHeight),
                new Rect(0, 0, end.Size.Width, end.Size.Height), tint, alpha);
        }

        if (showHead && head is not null)
        {
            var headWidth = head.Size.Width * unit;
            var headHeight = head.Size.Height * unitY;
            var headTop = direction > 0 ? headY : headY - headHeight;
            DrawTinted(drawing, head,
                new Rect(centerX - headWidth / 2, headTop, headWidth, headHeight),
                new Rect(0, 0, head.Size.Width, head.Size.Height), tint, alpha);
        }
    }

    private void DrawPackHold(CanvasDrawingSession drawing, NoteSkin skin, float centerX, float headY, float tailY,
        float width, float heightWidth, bool highlight, bool showHead, bool isUp,
        IReadOnlyList<double>? tint, float alpha)
    {
        var bitmap = _packRenderer[highlight ? "hold_mh.png" : "hold.png"];
        if (bitmap is null)
        {
            return;
        }

        var atlas = highlight ? skin.Info.HoldAtlasMH : skin.Info.HoldAtlas;
        var tailPixels = Math.Clamp(atlas[0], 0, (int)bitmap.Size.Height - 1);
        var headPixels = Math.Clamp(atlas[1], 0, (int)bitmap.Size.Height - tailPixels);
        var bodyPixels = (int)bitmap.Size.Height - tailPixels - headPixels;
        var unit = width / Math.Max(1, bitmap.Size.Width);
        var unitY = heightWidth / Math.Max(1, bitmap.Size.Width);
        var length = Math.Abs(tailY - headY);
        var direction = length < 0.01 ? (isUp ? 1 : -1) : tailY < headY ? 1 : -1;

        var old = drawing.Transform;
        // 参考 skin.mjs hold：先 translate(centerX, headY) 再 scale(1, direction)。
        // Win2D 的行向量矩阵需要反序拼接，否则 Hold 会绕画布原点翻转而倾斜/错位。
        drawing.Transform = Matrix3x2.CreateScale(1, direction) *
                            Matrix3x2.CreateTranslation(centerX, headY) * old;

        if (bodyPixels > 0 && length > 0)
        {
            DrawTinted(drawing, bitmap,
                new Rect(-width / 2, -length, width, Math.Max(1, length)),
                new Rect(0, tailPixels, bitmap.Size.Width, bodyPixels), tint, alpha);
        }

        if (tailPixels > 0)
        {
            var endHeight = tailPixels * unitY;
            DrawTinted(drawing, bitmap,
                new Rect(-width / 2, -length - endHeight, width, endHeight),
                new Rect(0, 0, bitmap.Size.Width, tailPixels), tint, alpha);
        }

        if (showHead && headPixels > 0)
        {
            var headHeight = headPixels * unitY;
            DrawTinted(drawing, bitmap,
                new Rect(-width / 2, 0, width, headHeight),
                new Rect(0, bitmap.Size.Height - headPixels, bitmap.Size.Width, headPixels), tint, alpha);
        }

        drawing.Transform = old;
    }

    private void DrawHits(CanvasDrawingSession drawing, IReadOnlyList<LineState> states,
        float originX, float originY, float scale, double seconds, PreviewViewport viewport)
    {
        var defaultTint = new[] { 255.0, 236.0, 160.0 };
        var pack = _packRenderer.IsReady ? _packRenderer.Skin : null;
        var aspectRatio = viewport.Height > 0 ? viewport.Width / viewport.Height : 1.5;
        var positionScale = PreviewViewport.PositionScale(aspectRatio);
        const double lifetime = 2.0 / 3.0;
        var samples = new Dictionary<long, Func<int, LineState?>>();

        Func<int, LineState?> SampleAt(double time)
        {
            var key = (long)Math.Round(time * 1_000_000);
            if (!samples.TryGetValue(key, out var result))
            {
                result = _scene.Sampler(time, positionScale);
                samples[key] = result;
            }

            return result;
        }

        foreach (var index in _scene.Order)
        {
            if (index >= _scene.Lines.Count)
            {
                continue;
            }

            var runtime = _scene.Lines[index];
            foreach (var hit in runtime.RecentHits(seconds, lifetime))
            {
                var entry = hit.Entry;
                var time = hit.Time;
                var age = seconds - time;
                if (age < 0 || age >= lifetime)
                {
                    continue;
                }

                var hitState = SampleAt(time)(index);
                if (hitState is null)
                {
                    continue;
                }

                var position = runtime.NoteState(entry, hitState, time);
                if (runtime.Line.Father < 0 && (positionScale.X != 1 || positionScale.Y != 1))
                {
                    position = position with
                    {
                        X = position.X * positionScale.X,
                        Y = position.Y * positionScale.Y,
                        Tail = position.Tail * positionScale.Y,
                    };
                }

                var angle = hitState.Rotation * Math.PI / 180;
                var worldX = hitState.X + position.X * Math.Cos(angle) + position.Y * Math.Sin(angle);
                var worldY = -hitState.Y + position.X * Math.Sin(angle) - position.Y * Math.Cos(angle);
                var x = originX + (float)(worldX * scale);
                var y = originY + (float)(worldY * scale);
                var size = (float)(LogicalNoteSize * 1.4 * scale);
                var tint = entry.Note.TintHitEffects ?? defaultTint;
                var hitBitmap = _packRenderer["hit_fx.png"];
                if (pack is not null && hitBitmap is not null)
                {
                    var perfect = pack.Info.ColorPerfect;
                    var perfectRgb = new[] { (double)((perfect >> 16) & 0xFF), (double)((perfect >> 8) & 0xFF), (double)(perfect & 0xFF) };
                    tint = pack.Info.HitFxTinted ? entry.Note.TintHitEffects ?? perfectRgb : new double[] { 255, 255, 255 };
                }

                if (age < (pack?.Info.HitFxDuration ?? HitFrameSeconds * HitFrameCount))
                {
                    var old = drawing.Transform;
                    if (pack is not null && hitBitmap is not null)
                    {
                        var columns = Math.Max(1, pack.Info.HitFx[0]);
                        var rows = Math.Max(1, pack.Info.HitFx[1]);
                        var total = columns * rows;
                        var progress = Math.Clamp(age / Math.Max(0.001, pack.Info.HitFxDuration), 0, 0.9999);
                        var frameIndex = Math.Min(total - 1, (int)Math.Floor(progress * total));
                        var frameWidth = hitBitmap.Size.Width / columns;
                        var frameHeight = hitBitmap.Size.Height / rows;
                        var effectSize = (float)(size * pack.Info.HitFxScale);
                        var source = new Rect(frameIndex % columns * frameWidth, frameIndex / columns * frameHeight, frameWidth, frameHeight);
                        var hitRect = new Rect(x - effectSize / 2, y - effectSize * (float)(frameHeight / frameWidth) / 2,
                            effectSize, effectSize * (float)(frameHeight / frameWidth));
                        if (InViewport(hitRect, viewport))
                        {
                            if (pack.Info.HitFxRotate)
                            {
                                drawing.Transform = Matrix3x2.CreateRotation((float)angle, new Vector2(x, y)) * old;
                            }

                            var envelope = progress < 0.5 ? 1 - progress * 0.6 : 1.4 * (1 - progress);
                            DrawTinted(drawing, hitBitmap, hitRect, source, tint, (float)Math.Clamp(envelope, 0, 1));
                            drawing.Transform = old;
                        }
                    }
                    else
                    {
                        var frame = Math.Min(HitFrameCount, (int)Math.Floor(age / HitFrameSeconds) + 1);
                        var bitmap = _textures[$"img-{frame}.png"];
                        if (bitmap is not null)
                        {
                            var destination = new Rect(x - size / 2, y - size / 2, size, size);
                            if (InViewport(destination, viewport))
                            {
                                drawing.Transform = Matrix3x2.CreateRotation((float)angle, new Vector2(x, y)) * old;
                                DrawTinted(drawing, bitmap, destination,
                                    new Rect(0, 0, bitmap.Size.Width, bitmap.Size.Height), tint, 1);
                                drawing.Transform = old;
                            }
                        }
                    }
                }

                if (pack?.Info.HideParticles == true)
                {
                    continue;
                }

                var amount = EasingMath.Curve(16, Math.Clamp(age * 1.5, 0, 1));
                for (var particle = 0; particle < 4; particle++)
                {
                    var random = Math.Sin(hit.Seed * 12.9898 + particle * 78.233) * 43758.5453;
                    var particleAngle = (random - Math.Floor(random)) * Math.PI * 2;
                    var px = x + (float)(Math.Cos(particleAngle) * size * 1.1 * amount);
                    var py = y + (float)(Math.Sin(particleAngle) * size * 1.1 * amount);
                    var radius = (float)(size * 0.075 * amount);
                    drawing.FillRectangle(new Rect(px - radius, py - radius, radius * 2, radius * 2),
                        WithAlpha(Rgb(tint), (float)Math.Max(0, 1 - age * 1.5)));
                }
            }
        }
    }

    private sealed class GuideGroup
    {
        public GuideGroup(float x, float y, float rotation, int index)
        {
            X = x;
            Y = y;
            Rotation = rotation;
            Indices.Add(index);
        }

        public float X { get; }
        public float Y { get; }
        public float Rotation { get; }
        public List<int> Indices { get; } = new();
    }

    /// <summary>
    /// 参考 preview.mjs drawGuides：中心线号 + 当前线的菱形方向箭头。
    /// 每条线只显示一个编号；当前线为加粗大号绿色，其余为白色；辅助层不受判定线透明度影响。
    /// </summary>
    private void DrawGuides(CanvasDrawingSession drawing, ICanvasResourceCreator creator,
        IReadOnlyList<LineState> states, float scale, int selectedLine, PreviewViewport viewport)
    {
        var originX = viewport.Left + viewport.Width / 2;
        var originY = viewport.Top + viewport.Height / 2;
        var selectedColor = Windows.UI.Color.FromArgb(255, 0, 200, 0);
        var normalColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
        var arrow = _textures["Arrow2.png"];
        var margin = 60 * scale;

        // 先画普通白色线号，最后画当前线绿色线号，保证它压在其他线号之上。
        for (var pass = 0; pass < 2; pass++)
        {
        for (var index = 0; index < states.Count; index++)
        {
            var state = states[index];
            if (!double.IsFinite(state.X) || !double.IsFinite(state.Y) || !double.IsFinite(state.Rotation))
            {
                continue;
            }

            var x = (float)(originX + state.X * scale);
            var y = (float)(originY - state.Y * scale);
            if (x < viewport.Left - margin || x > viewport.Left + viewport.Width + margin ||
                y < viewport.Top - margin || y > viewport.Top + viewport.Height + margin)
            {
                continue;
            }

            var selected = index == selectedLine;
            if (pass == 0 ? selected : !selected)
            {
                continue;
            }

            var label = index.ToString(CultureInfo.InvariantCulture);
            var fontSize = Math.Max(1, (int)Math.Round((selected ? 48 : 30) * scale));

            var oldTransform = drawing.Transform;
            var baseTransform = Matrix3x2.CreateRotation((float)(state.Rotation * Math.PI / 180)) *
                                Matrix3x2.CreateTranslation(x, y) * oldTransform;

            if (selected)
            {
                drawing.Transform = Matrix3x2.CreateRotation((float)Math.PI) * baseTransform;
                if (arrow is not null)
                {
                    var size = 50 * scale;
                    drawing.DrawImage(arrow,
                        new Rect(-size / 2, -size / 2, size, size),
                        new Rect(0, 0, arrow.Size.Width, arrow.Size.Height), 1);
                }
                else
                {
                    drawing.DrawLine(new Vector2(0, -24 * scale), new Vector2(-12 * scale, -8 * scale), normalColor, 2);
                    drawing.DrawLine(new Vector2(0, -24 * scale), new Vector2(12 * scale, -8 * scale), normalColor, 2);
                }
            }

            drawing.Transform = baseTransform;
            var textY = (float)((selected ? 8 : 4) * scale);
            _fontAtlas.Draw(drawing, label, new Vector2(0, textY), fontSize,
                selected ? selectedColor : normalColor, center: true);
            drawing.Transform = oldTransform;
        }
        }
    }

    private string FormatLineNumbers(IEnumerable<int> indices)
    {
        var sorted = indices.OrderBy(index => index).ToArray();
        var parts = new List<string>();
        for (var index = 0; index < sorted.Length; index++)
        {
            var first = sorted[index];
            var last = first;
            while (index + 1 < sorted.Length && sorted[index + 1] == last + 1 &&
                   _scene.Lines[first].Line.Father < 0 && _scene.Lines[sorted[index + 1]].Line.Father < 0)
            {
                last = sorted[++index];
            }

            var father = _scene.Lines[first].Line.Father;
            parts.Add(last == first
                ? father >= 0 ? $"{first}({father})" : first.ToString(CultureInfo.InvariantCulture)
                : $"{first}–{last}");
        }

        return string.Join(", ", parts);
    }

    private static bool InViewport(Rect rect, PreviewViewport viewport) =>
        rect.Right >= viewport.Left && rect.Left <= viewport.Left + viewport.Width &&
        rect.Bottom >= viewport.Top && rect.Top <= viewport.Top + viewport.Height;

    private static double[] DefaultNoteColor(NoteType type) => type switch
    {
        NoteType.Flick => new[] { 245.0, 150.0, 172.0 },
        NoteType.Drag => new[] { 241.0, 206.0, 118.0 },
        _ => new[] { 138.0, 203.0, 255.0 },
    };

    private void DrawTinted(CanvasDrawingSession drawing, CanvasBitmap bitmap, Rect destination, Rect source,
        IReadOnlyList<double>? color, float opacity)
    {
        if (color is null || color.Count < 3 || color.All(value => Math.Abs(value - 255) < 1e-6) || opacity <= 0)
        {
            drawing.DrawImage(bitmap, destination, source, opacity);
            return;
        }

        if (_creator is null)
        {
            drawing.DrawImage(bitmap, destination, source, opacity);
            return;
        }

        var tinted = _tintCache.Get(_creator, bitmap, source, color);
        drawing.DrawImage(tinted, destination,
            new Rect(0, 0, tinted.Size.Width, tinted.Size.Height), opacity);
    }

    private static Windows.UI.Color Rgb(IReadOnlyList<double> color) =>
        Windows.UI.Color.FromArgb(255,
            (byte)Math.Clamp(color.Count > 0 ? color[0] : 255, 0, 255),
            (byte)Math.Clamp(color.Count > 1 ? color[1] : 255, 0, 255),
            (byte)Math.Clamp(color.Count > 2 ? color[2] : 255, 0, 255));

    private static Windows.UI.Color WithAlpha(Windows.UI.Color color, float alpha) =>
        Windows.UI.Color.FromArgb((byte)Math.Clamp(alpha * 255, 0, 255), color.R, color.G, color.B);
}

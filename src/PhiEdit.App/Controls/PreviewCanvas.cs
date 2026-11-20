using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhiEdit.App.Services;
using PhiEdit.Core.Charts;
using PhiEdit.Core.Editing;
using PhiEdit.Core.IO;
using PhiEdit.Core.Preview;
using PhiEdit.Core.Skins;
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
    private const double HoldHitInterval = 20.0 / 120.0;

    private readonly CanvasControl _canvas = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
    };

    private readonly SceneRuntime _scene = new();
    private readonly RpeTextureRenderer _textures = new();
    private readonly NoteSkinRenderer _packRenderer = new();
    private readonly Dictionary<string, CanvasBitmap> _assetTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loadingTextures = new(StringComparer.OrdinalIgnoreCase);
    private EditorSession? _session;
    private IPreviewTimeSource? _clock;
    private string? _loadedPackId;
    private bool _packLoading;
    private CanvasBitmap? _background;
    private string? _backgroundKey;
    private bool _backgroundLoading;
    private bool _loading;

    public PreviewCanvas()
    {
        Content = _canvas;
        _canvas.Draw += OnDraw;
        _canvas.CreateResources += (sender, _) => EnsureTexturesLoaded(sender);
        _canvas.SizeChanged += (_, _) => Invalidate();
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

    public void Invalidate() => _canvas.Invalidate();

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        _scene.MarkDirty();
        Invalidate();
    }

    private void OnSkinChanged(object? sender, EventArgs e)
    {
        _packRenderer.Reset();
        _loadedPackId = null;
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
        _ = LoadPackAsync(creator, skin);
    }

    private async Task LoadPackAsync(ICanvasResourceCreator creator, NoteSkin skin)
    {
        try
        {
            _packRenderer.Reset();
            await _packRenderer.ApplyAsync(creator, skin);
            _loadedPackId = skin.Id;
            Invalidate();
        }
        catch
        {
            _loadedPackId = null;
        }
        finally
        {
            _packLoading = false;
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
            Invalidate();
        }
        finally
        {
            _loading = false;
        }
    }

    private void EnsureBackgroundLoaded(ICanvasResourceCreator creator, string? name)
    {
        if (string.IsNullOrEmpty(name) || name == _backgroundKey || _backgroundLoading)
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
        _ = LoadBackgroundAsync(creator, bytes, name);
    }

    private async Task LoadBackgroundAsync(ICanvasResourceCreator creator, byte[] bytes, string name)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var random = stream.AsRandomAccessStream();
            _background?.Dispose();
            _background = await CanvasBitmap.LoadAsync(creator, random);
            Invalidate();
        }
        catch
        {
            _background = null;
        }
        finally
        {
            if (_backgroundKey == name)
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

        _ = LoadLineTextureAsync(creator, name, bytes);
    }

    private async Task LoadLineTextureAsync(ICanvasResourceCreator creator, string name, byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var random = stream.AsRandomAccessStream();
            _assetTextures[name] = await CanvasBitmap.LoadAsync(creator, random);
            Invalidate();
        }
        catch
        {
            // 无法解码的贴图按默认线处理。
        }
        finally
        {
            _loadingTextures.Remove(name);
        }
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        var width = (float)Math.Max(1, sender.ActualWidth);
        var height = (float)Math.Max(1, sender.ActualHeight);
        var drawing = args.DrawingSession;
        drawing.Clear(Windows.UI.Color.FromArgb(255, 12, 14, 19));

        EnsureTexturesLoaded(sender);
        EnsurePackLoaded(sender);
        var viewport = PreviewViewport.Create(width, height);
        var backgroundName = session.Assets.FindBackground(session.Document.Background);
        EnsureBackgroundLoaded(sender, backgroundName);
        using (drawing.CreateLayer(1, new Rect(viewport.Left, viewport.Top, viewport.Width, viewport.Height)))
        {
            DrawBackground(drawing, width, height, viewport.Scale);
        }

        if (_background is null)
        {
            drawing.FillRectangle(
                new Rect(viewport.Left, viewport.Top, viewport.Width, viewport.Height),
                Windows.UI.Color.FromArgb(255, 22, 27, 38));
        }
        drawing.DrawRectangle(
            new Rect(viewport.Left, viewport.Top, viewport.Width, viewport.Height),
            Windows.UI.Color.FromArgb(255, 84, 96, 116));

        var tempo = session.Document.BuildTempoMap();
        _scene.Compile(session.Document, tempo);
        var seconds = _clock?.CurrentSeconds ?? tempo.SecondsAt(session.Viewport.Position);
        var states = _scene.Sample(seconds, PreviewViewport.PositionScale());
        var scale = (float)viewport.Scale;
        var originX = (float)(viewport.Left + viewport.Width / 2);
        var originY = (float)(viewport.Top + viewport.Height / 2);

        using (drawing.CreateLayer(1, new Rect(viewport.Left, viewport.Top, viewport.Width, viewport.Height)))
        {
            DrawPass(drawing, sender, PassKind.Line, states, originX, originY, scale, seconds);
            DrawPass(drawing, sender, PassKind.Hold, states, originX, originY, scale, seconds);
            DrawPass(drawing, sender, PassKind.Note, states, originX, originY, scale, seconds);
            DrawHits(drawing, states, originX, originY, scale, seconds, viewport);
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
        var blur = new GaussianBlurEffect { Source = _background, BlurAmount = 10.5f };
        drawing.DrawImage(blur, destination, source, 0.35f);
    }

    private enum PassKind { Line, Hold, Note }

    private void DrawPass(CanvasDrawingSession drawing, ICanvasResourceCreator creator, PassKind pass,
        IReadOnlyList<LineState> states, float originX, float originY, float scale, double seconds)
    {
        foreach (var index in _scene.Order)
        {
            if (index < 0 || index >= states.Count)
            {
                continue;
            }

            var runtime = _scene.Lines[index];
            var state = states[index];
            if (state.Alpha <= 0 || state.ScaleX == 0 || state.ScaleY == 0)
            {
                continue;
            }

            var centerX = originX + (float)(state.X * scale);
            var centerY = originY - (float)(state.Y * scale);
            var oldTransform = drawing.Transform;
            drawing.Transform = Matrix3x2.CreateRotation((float)(state.Rotation * Math.PI / 180), new Vector2(centerX, centerY));

            if (pass == PassKind.Line)
            {
                DrawLine(drawing, creator, runtime, state, centerX, centerY, scale);
            }
            else
            {
                var entries = runtime.VisibleNotes(seconds, state, 2000 / Math.Max(0.01, Math.Abs(runtime.Line.Speed)));
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

                    var noteX = centerX + (float)(position.X * scale);
                    var headY = centerY - (float)(position.Y * scale);
                    var tailY = centerY - (float)(position.Tail * scale);
                    var noteWidth = (float)(LogicalNoteSize * scale * position.Size);
                    var heightWidth = (float)(LogicalNoteSize * scale * position.SizeY);
                    var highlight = _scene.Simultaneous.Contains(entry.Note);
                    var tint = entry.Note.Tint ?? entry.Note.Color ?? DefaultNoteColor(entry.Note.Type);
                    var pack = _packRenderer.IsReady ? _packRenderer.Skin : null;
                    var textureScale = highlight && pack is not null ? pack.HighlightScale : 1;
                    noteWidth *= (float)textureScale;
                    heightWidth *= (float)textureScale;

                    var noteTransform = drawing.Transform;
                    if (Math.Abs(position.Skew) > 1e-6)
                    {
                        var skew = new Matrix3x2(1, 0, (float)Math.Tan(position.Skew * Math.PI / 180), 1, 0, 0);
                        drawing.Transform = Matrix3x2.CreateTranslation(noteX, headY) * skew *
                                            Matrix3x2.CreateTranslation(-noteX, -headY) * noteTransform;
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
        LineRuntime runtime, LineState state, float centerX, float centerY, float scale)
    {
        var line = runtime.Line;
        var alpha = (float)Math.Clamp(state.Alpha / 255, 0, 1);
        var tint = Rgb(state.Color);
        if (alpha <= 0)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(state.Text))
        {
            DrawLineText(drawing, state.Text, line, centerX, centerY, scale, tint, alpha);
            return;
        }

        var isDefault = string.IsNullOrEmpty(line.Texture) || line.Texture == "line.png";
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

        DrawTinted(drawing, bitmap, destination, new Rect(0, 0, bitmap.Size.Width, bitmap.Size.Height), state.Color, alpha);
    }

    private static void DrawLineText(CanvasDrawingSession drawing, string text, JudgeLine line,
        float centerX, float centerY, float scale, Windows.UI.Color color, float alpha)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var fontSize = (float)(52 * scale);
        var lineHeight = fontSize * 1.25f;
        var maxWidth = lines.Max(value => value.Length) * fontSize * 0.55f;
        var blockHeight = lines.Length * lineHeight;
        var left = centerX - maxWidth * (float)line.AnchorX;
        var top = centerY - blockHeight * (float)(1 - line.AnchorY);
        var format = CanvasExtensions.TextFormat(fontSize);
        for (var index = 0; index < lines.Length; index++)
        {
            drawing.DrawText(lines[index], new Vector2(left, top + index * lineHeight), WithAlpha(color, alpha), format);
        }
    }

    private void DrawNote(CanvasDrawingSession drawing, Note note, float centerX, float headY, float tailY,
        float width, float heightWidth, bool highlight, bool showHead, double[] tint, float alpha)
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
            drawing.FillRectangle(new Rect(centerX - width / 2, headY - 2, width, 4), Rgb(tint));
            return;
        }

        var height = Math.Max(5, heightWidth * bitmap.Size.Height / Math.Max(1, bitmap.Size.Width));
        var destination = new Rect(centerX - width / 2, headY - height / 2, width, height);
        DrawTinted(drawing, bitmap, destination, new Rect(0, 0, bitmap.Size.Width, bitmap.Size.Height), tint, alpha);
    }

    private void DrawHold(CanvasDrawingSession drawing, Note note, float centerX, float headY, float tailY,
        float width, float heightWidth, bool highlight, bool showHead, double[] tint, float alpha)
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
        IReadOnlyList<double> tint, float alpha)
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
        drawing.Transform = Matrix3x2.CreateTranslation(centerX, headY) *
                            Matrix3x2.CreateScale(1, direction) * old;

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
        foreach (var index in _scene.Order)
        {
            if (index >= _scene.Lines.Count)
            {
                continue;
            }

            var runtime = _scene.Lines[index];
            foreach (var entry in runtime.Notes)
            {
                foreach (var time in HitTimes(entry))
                {
                    var age = seconds - time;
                    if (age < 0 || age >= 2.0 / 3.0)
                    {
                        continue;
                    }

                    var hitStates = _scene.Sample(time);
                    if (index >= hitStates.Count)
                    {
                        continue;
                    }

                    var hitState = hitStates[index];
                    var position = runtime.NoteState(entry, hitState, time);
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

                    var seed = index * 65537 + entry.Index;
                    var amount = EasingMath.Curve(16, Math.Clamp(age * 1.5, 0, 1));
                    for (var particle = 0; particle < 4; particle++)
                    {
                        var random = Math.Sin(seed * 12.9898 + particle * 78.233) * 43758.5453;
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
    }

    private static IEnumerable<double> HitTimes(NoteEntry entry)
    {
        if (entry.Note.Type != NoteType.Hold)
        {
            yield return entry.Start;
            yield break;
        }

        for (var time = entry.Start; time <= entry.End + 1e-9; time += HoldHitInterval)
        {
            yield return time;
        }

        if (entry.End - entry.Start > 1e-9 && Math.Abs((entry.End - entry.Start) % HoldHitInterval) > 1e-9)
        {
            yield return entry.End;
        }
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

    private static void DrawTinted(CanvasDrawingSession drawing, CanvasBitmap bitmap, Rect destination, Rect source,
        IReadOnlyList<double>? color, float opacity)
    {
        if (color is null || color.Count < 3 || color.All(value => Math.Abs(value - 255) < 1e-6) || opacity <= 0)
        {
            drawing.DrawImage(bitmap, destination, source, opacity);
            return;
        }

        var matrix = new Matrix5x4
        {
            M11 = (float)(Math.Clamp(color[0], 0, 255) / 255),
            M22 = (float)(Math.Clamp(color[1], 0, 255) / 255),
            M33 = (float)(Math.Clamp(color[2], 0, 255) / 255),
            M44 = 1,
        };
        var effect = new ColorMatrixEffect { Source = bitmap, ColorMatrix = matrix };
        drawing.DrawImage(effect, destination, source, opacity);
    }

    private static Windows.UI.Color Rgb(IReadOnlyList<double> color) =>
        Windows.UI.Color.FromArgb(255,
            (byte)Math.Clamp(color.Count > 0 ? color[0] : 255, 0, 255),
            (byte)Math.Clamp(color.Count > 1 ? color[1] : 255, 0, 255),
            (byte)Math.Clamp(color.Count > 2 ? color[2] : 255, 0, 255));

    private static Windows.UI.Color WithAlpha(Windows.UI.Color color, float alpha) =>
        Windows.UI.Color.FromArgb((byte)Math.Clamp(alpha * 255, 0, 255), color.R, color.G, color.B);
}

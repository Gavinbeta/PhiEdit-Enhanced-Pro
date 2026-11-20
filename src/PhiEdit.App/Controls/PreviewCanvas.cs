using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhiEdit.App.Services;
using PhiEdit.Core.Charts;
using PhiEdit.Core.Editing;
using PhiEdit.Core.Preview;
using System.Numerics;
using Windows.Foundation;

namespace PhiEdit.App.Controls;

/// <summary>
/// 实时预览画布。始终渲染整份谱面的全部判定线，不受当前编辑线影响；
/// 画布本身不处理任何指针输入，仅作为只读预览层。
/// </summary>
public sealed class PreviewCanvas : UserControl
{
    private const double LogicalNoteSize = 175;
    private const double DefaultLineWidth = 4000;
    private const double DefaultLineHeight = 5 * 1.5;

    private readonly CanvasControl _canvas = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
    };

    private readonly SceneRuntime _scene = new();
    private readonly RpeTextureRenderer _textures = new();
    private EditorSession? _session;
    private IPreviewTimeSource? _clock;
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
            }

            _session = value;
            if (_session is not null)
            {
                _session.Changed += OnSessionChanged;
            }

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

        var viewport = PreviewViewport.Create(width, height);
        drawing.FillRectangle(
            new Rect(viewport.Left, viewport.Top, viewport.Width, viewport.Height),
            Windows.UI.Color.FromArgb(255, 22, 27, 38));
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
            DrawPass(drawing, PassKind.Line, states, originX, originY, scale, seconds);
            DrawPass(drawing, PassKind.Hold, states, originX, originY, scale, seconds);
            DrawPass(drawing, PassKind.Note, states, originX, originY, scale, seconds);
            DrawHits(drawing, states, originX, originY, scale, seconds, viewport);
        }

        var format = CanvasExtensions.TextFormat(12);
        drawing.DrawText("实时预览 · 整份谱面", new Vector2(10, 8),
            Windows.UI.Color.FromArgb(190, 210, 220, 235), format);
    }

    private enum PassKind { Line, Hold, Note }

    private void DrawPass(CanvasDrawingSession drawing, PassKind pass, IReadOnlyList<LineState> states,
        float originX, float originY, float scale, double seconds)
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
                DrawLine(drawing, runtime.Line, state, centerX, centerY, scale);
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
                    DrawNote(drawing, entry.Note, noteX, headY, tailY, noteWidth, heightWidth,
                        highlight, position.ShowHead, entry.Note.Color, (float)Math.Clamp(position.Alpha, 0, 1));
                }
            }

            drawing.Transform = oldTransform;
        }
    }

    private void DrawLine(CanvasDrawingSession drawing, JudgeLine line, LineState state,
        float centerX, float centerY, float scale)
    {
        var alpha = (float)Math.Clamp(state.Alpha / 255, 0, 1);
        var tint = Rgb(state.Color);
        if (alpha <= 0)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(state.Text))
        {
            var format = CanvasExtensions.TextFormat((float)(52 * scale));
            var left = centerX - (float)(300 * line.AnchorX * scale);
            drawing.DrawText(state.Text, new Vector2(left, centerY - 26 * scale), WithAlpha(tint, alpha), format);
            return;
        }

        var width = (float)(DefaultLineWidth * scale * state.ScaleX);
        var height = (float)(DefaultLineHeight * scale * state.ScaleY);
        var leftEdge = centerX - (float)(width * line.AnchorX);
        var top = centerY - (float)(height * (1 - line.AnchorY));
        var destination = new Rect(leftEdge, top, Math.Abs(width), Math.Max(1, Math.Abs(height)));

        var bitmap = _textures["line.png"];
        if (bitmap is null)
        {
            drawing.FillRectangle(destination, WithAlpha(tint, alpha));
            return;
        }

        DrawTinted(drawing, bitmap, destination, new Rect(0, 0, bitmap.Size.Width, bitmap.Size.Height), state.Color, alpha);
    }

    private void DrawNote(CanvasDrawingSession drawing, Note note, float centerX, float headY, float tailY,
        float width, float heightWidth, bool highlight, bool showHead, double[]? tint, float alpha)
    {
        if (note.Type == NoteType.Hold)
        {
            DrawHold(drawing, centerX, headY, tailY, width, heightWidth, highlight, showHead, tint, alpha);
            return;
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
            return;
        }

        var height = Math.Max(5, heightWidth * bitmap.Size.Height / Math.Max(1, bitmap.Size.Width));
        var destination = new Rect(centerX - width / 2, headY - height / 2, width, height);
        DrawTinted(drawing, bitmap, destination, new Rect(0, 0, bitmap.Size.Width, bitmap.Size.Height), tint, alpha);
    }

    private void DrawHold(CanvasDrawingSession drawing, float centerX, float headY, float tailY,
        float width, float heightWidth, bool highlight, bool showHead, double[]? tint, float alpha)
    {
        var bodyName = highlight ? "HoldHL.png" : "Hold3.png";
        var body = _textures[bodyName] ?? _textures["Hold.png"];
        var end = _textures["HoldEnd.png"];
        var headName = highlight ? "HoldHeadHL.png" : "HoldHead.png";
        var head = _textures[headName];
        if (body is null)
        {
            return;
        }

        var direction = tailY < headY ? 1 : -1;
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
            DrawTinted(drawing, head,
                new Rect(centerX - headWidth / 2, headY, headWidth, headHeight),
                new Rect(0, 0, head.Size.Width, head.Size.Height), tint, alpha);
        }
    }

    private void DrawHits(CanvasDrawingSession drawing, IReadOnlyList<LineState> states,
        float originX, float originY, float scale, double seconds, PreviewViewport viewport)
    {
        const double frameSeconds = 0.015;
        foreach (var index in _scene.Order)
        {
            if (index >= _scene.Lines.Count)
            {
                continue;
            }

            var runtime = _scene.Lines[index];
            foreach (var entry in runtime.Notes)
            {
                var time = entry.Note.Type == NoteType.Hold ? entry.End : entry.Start;
                var age = seconds - time;
                if (age < 0 || age >= frameSeconds * 31)
                {
                    continue;
                }

                var frame = Math.Min(31, (int)Math.Floor(age / frameSeconds) + 1);
                var bitmap = _textures[$"img-{frame}.png"];
                if (bitmap is null)
                {
                    continue;
                }

                var hitState = _scene.Sample(time)[index];
                var position = runtime.NoteState(entry, hitState, time);
                var angle = hitState.Rotation * Math.PI / 180;
                var worldX = hitState.X + position.X * Math.Cos(angle) + position.Y * Math.Sin(angle);
                var worldY = -hitState.Y + position.X * Math.Sin(angle) - position.Y * Math.Cos(angle);
                var x = originX + (float)(worldX * scale);
                var y = originY + (float)(worldY * scale);
                var size = (float)(LogicalNoteSize * 1.4 * scale);
                var destination = new Rect(x - size / 2, y - size / 2, size, size);
                if (destination.Right < viewport.Left || destination.Left > viewport.Left + viewport.Width ||
                    destination.Bottom < viewport.Top || destination.Top > viewport.Top + viewport.Height)
                {
                    continue;
                }

                drawing.DrawImage(bitmap, destination, new Rect(0, 0, bitmap.Size.Width, bitmap.Size.Height), 1);
            }
        }
    }

    private static void DrawTinted(CanvasDrawingSession drawing, CanvasBitmap bitmap, Rect destination, Rect source,
        double[]? color, float opacity)
    {
        if (color is null || color.Length < 3 || color.All(value => Math.Abs(value - 255) < 1e-6) || opacity <= 0)
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

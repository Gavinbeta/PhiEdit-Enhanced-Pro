using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PhiEdit.App.Services;
using PhiEdit.Core.Beats;
using PhiEdit.Core.Charts;
using PhiEdit.Core.Editing;
using PhiEdit.Core.Layout;
using PhiEdit.Core.Rendering;
using PhiEdit.Core.Skins;
using System.Numerics;
using Windows.Foundation;

namespace PhiEdit.App.Controls;

/// <summary>
/// 唯一的编辑画布：一个 CanvasControl 同时绘制左侧音符区与右侧判定线区。
///
/// 两侧共用同一个 EditViewport、同一套网格参数与同一个选中状态；中间只保留一条
/// 窄槽用于分隔和拍号，不存在两块独立画布，因此不会出现双重网格线或滚动不同步。
/// </summary>
public sealed class EditorCanvas : UserControl
{
    private const double GutterWidth = 24;
    private const double HeaderHeight = 24;
    private const double FooterHeight = 26;
    private const double DragThreshold = 3;

    private readonly CanvasControl _canvas = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
    };

    private EditorSession? _session;
    private ChartEditor? _editor;

    private double _splitRatio = 0.5;

    private Note? _dragNote;
    private LineEvent? _dragEvent;
    private double _pressX;
    private double _pressY;
    private bool _noteDragMoved;
    private bool _eventDragMoved;

    private readonly List<NoteDragOrigin> _noteOrigins = new();
    private readonly List<EventDragOrigin> _eventOrigins = new();

    private enum MarqueeArea { None, Notes, Events }

    private MarqueeArea _marqueeArea;
    private double _marqueeStartX;
    private double _marqueeStartY;
    private double _marqueeCurrentX;
    private double _marqueeCurrentY;
    private bool _marqueeAppend;

    private Point _lastPointerPosition;
    private bool _holdPending;
    private double _holdStartBeat;
    private double _holdStageX;
    private bool _eventPending;
    private double _eventStartBeat;
    private int _eventChannel;

    private readonly record struct NoteDragOrigin(Note Note, double Beat, double End, double X);
    private readonly record struct EventDragOrigin(LineEvent Event, double Start, double End, double StartValue, double EndValue);

    public EditorCanvas()
    {
        Content = _canvas;
        _canvas.Draw += OnDraw;
        _canvas.CreateResources += (sender, _) => EnsureSkinLoaded(sender);
        _canvas.SizeChanged += (_, _) => Invalidate();
        _canvas.PointerPressed += OnPointerPressed;
        _canvas.PointerMoved += OnPointerMoved;
        _canvas.PointerReleased += OnPointerReleased;
        _canvas.PointerCanceled += OnPointerReleased;
        _canvas.PointerCaptureLost += (_, _) => EndDrag();
        _canvas.DoubleTapped += OnDoubleTapped;
        _canvas.IsTabStop = true;
    }

    public EditorPalette Palette { get; set; } = new();

    public NoteSkinRenderer? Renderer { get; set; }

    public double NoteSize { get; set; } = 200;

    public ICanvasResourceCreator CanvasResourceCreator => _canvas;

    public IReadOnlyList<EventTrackKind> Tracks { get; set; } = EventTrackKinds.Standard;

    /// <summary>特殊层显示扩展轨道，基础层显示五条标准轨道。</summary>
    private IReadOnlyList<EventTrackKind> ActiveTracks =>
        _session?.CurrentLayer == JudgeLine.SpecialLayer ? EventTrackKinds.Extended : Tracks;

    public event EventHandler<string>? SkinLoadFailed;

    public event EventHandler? SelectionChanged;

    public EditorSession? Session
    {
        get => _session;
        set
        {
            if (_session is not null)
            {
                _session.Viewport.ViewChanged -= OnViewChanged;
            }

            _session = value;

            if (_session is not null)
            {
                _session.Viewport.ViewChanged += OnViewChanged;
            }

            Invalidate();
        }
    }

    public ChartEditor? Editor
    {
        get => _editor;
        set => _editor = value;
    }

    /// <summary>左右半区分割位置（0–1），由主窗口的可拖动分隔条更新。</summary>
    public double SplitRatio
    {
        get => _splitRatio;
        set
        {
            _splitRatio = Math.Clamp(value, 0.15, 0.85);
            Invalidate();
        }
    }

    private void OnViewChanged(object? sender, EventArgs e) => Invalidate();

    public void Invalidate() => _canvas.Invalidate();

    private bool _skinLoading;
    private bool _skinReloadPending;

    public void RequestSkinReload()
    {
        _skinReloadPending = true;
        Invalidate();
    }

    private void EnsureSkinLoaded(ICanvasResourceCreator creator)
    {
        var skin = _session?.Skins.Active;
        var renderer = Renderer;
        if (skin is null || renderer is null || _skinLoading)
        {
            return;
        }

        if (!_skinReloadPending && renderer.IsReady && renderer.Skin?.Id == skin.Id)
        {
            return;
        }

        _skinReloadPending = false;
        _skinLoading = true;
        _ = LoadSkinAsync(creator, skin, renderer);
    }

    private async Task LoadSkinAsync(ICanvasResourceCreator creator, NoteSkin skin, NoteSkinRenderer renderer)
    {
        try
        {
            await renderer.ApplyAsync(creator, skin);
            Invalidate();
        }
        catch (Exception exception)
        {
            SkinLoadFailed?.Invoke(this, $"皮肤加载失败：{exception.Message}");
        }
        finally
        {
            _skinLoading = false;
        }
    }

    private (double Split, double LeftWidth, double RightLeft, double RightWidth) Layout(double width)
    {
        var split = width * _splitRatio;
        var leftWidth = Math.Max(80, split - GutterWidth / 2);
        var rightLeft = Math.Min(width - 80, split + GutterWidth / 2);
        return (split, leftWidth, rightLeft, Math.Max(80, width - rightLeft));
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var session = _session;
        var drawing = args.DrawingSession;
        drawing.Clear(Palette.Surface.ToWinColor());

        var width = _canvas.ActualWidth;
        var height = _canvas.ActualHeight;
        if (session is null || width <= 0 || height <= 0)
        {
            return;
        }

        EnsureSkinLoaded(sender);

        var viewport = session.Viewport;
        viewport.ViewportHeight = height;

        var layout = Layout(width);
        // 竖线网格与红/绿横线使用同一段可用宽度：左右各让出 GridInsetX。
        viewport.NoteWidth = Math.Max(80, layout.LeftWidth - Palette.GridInsetX * 2);

        // 两侧底色一致，中间不再画灰色区域，视觉上是一块连续画布。
        drawing.FillRectangle(new Rect(0, 0, (float)width, (float)height), Palette.Surface.ToWinColor());

        var playheadY = viewport.PlayheadY;
        DrawTimeGrid(drawing, session, viewport, 0, layout.LeftWidth, playheadY,
            layout.LeftWidth + GutterWidth / 2);
        DrawVerticalGrid(drawing, session.GridCount, 0, layout.LeftWidth, playheadY);
        DrawNotes(drawing, session, viewport, 0, layout.LeftWidth, height);
        DrawPendingHold(drawing, session, viewport, 0, layout.LeftWidth, height);

        DrawTimeGrid(drawing, session, viewport, layout.RightLeft, layout.RightWidth, playheadY,
            width - 14);
        DrawVerticalGrid(drawing, 6, layout.RightLeft, layout.RightWidth, playheadY);
        DrawEvents(drawing, session, viewport, layout.RightLeft, layout.RightWidth, height);
        DrawPendingEvent(drawing, session, viewport, layout.RightLeft, layout.RightWidth, height);
        DrawHeader(drawing, layout.RightLeft, layout.RightWidth);

        DrawPlayhead(drawing, 0, layout.LeftWidth, playheadY);
        DrawPlayhead(drawing, layout.RightLeft, layout.RightWidth, playheadY);
        DrawEventValues(drawing, session, layout.RightLeft, layout.RightWidth, playheadY);
        DrawMarquee(drawing);
    }

    private void DrawVerticalGrid(CanvasDrawingSession drawing, int count,
        double left, double width, double playheadY)
    {
        var grid = new VerticalGrid(count);
        var gridTop = (float)Palette.GridInsetTop;
        // 竖线一直画到判定线；灰线会盖住它压在判定线上的部分。
        var gridBottom = (float)Math.Max(gridTop, playheadY);
        var gridLeft = left + Palette.GridInsetX;
        var gridRight = left + width - Palette.GridInsetX;
        var span = Math.Max(1, gridRight - gridLeft);
        foreach (var stageX in grid.Lines())
        {
            var x = (float)(gridLeft + (stageX + VerticalGrid.StageHalfWidth) / VerticalGrid.StageWidth * span);
            if (x < gridLeft - 2 || x > gridRight + 2)
            {
                continue;
            }

            var center = Math.Abs(stageX) < 1e-6;
            var strokeWidth = center && grid.HasCenterLine ? Palette.VerticalCenterWidth : Palette.VerticalWidth;
            drawing.DrawLine(x, gridTop, x, gridBottom,
                EditorPalette.WithAlpha(Palette.VerticalLine, Palette.VerticalAlpha).ToWinColor(), strokeWidth);
        }
    }

    private void DrawTimeGrid(CanvasDrawingSession drawing, EditorSession session, EditViewport viewport,
        double left, double width, double playheadY, double labelX)
    {
        var gridLeft = (float)(left + Palette.GridInsetX);
        var gridRight = (float)Math.Max(gridLeft + 1, left + width - Palette.GridInsetX);
        var gridTop = Palette.GridInsetTop;
        var gridBottom = Math.Max(gridTop, playheadY - Palette.GridInsetBottom);
        var format = CanvasExtensions.TextFormat(22f, center: true, bold: true);

        foreach (var tick in GridLayout.Ticks(viewport.BottomBeat - 1, viewport.TopBeat + 1, session.Division))
        {
            if (tick.Beat < 0)
            {
                continue;
            }

            var y = viewport.YAtBeat(tick.Beat);
            if (y < gridTop - 4 || y > gridBottom + 4)
            {
                continue;
            }

            switch (tick.Kind)
            {
                case GridTickKind.Beat:
                    drawing.DrawLine(gridLeft, (float)y, gridRight, (float)y, Palette.BeatLine.ToWinColor(), Palette.BeatLineWidth);
                    drawing.DrawText(tick.Beat.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                        new Vector2((float)labelX, (float)(y - 12)), Palette.BeatLabel.ToWinColor(), format);
                    break;
                case GridTickKind.Subdivision:
                    drawing.DrawLine(gridLeft, (float)y, gridRight, (float)y,
                        EditorPalette.WithAlpha(Palette.Subdivision, Palette.SubdivisionAlpha).ToWinColor(),
                        Palette.SubdivisionWidth);
                    break;
                default:
                    drawing.DrawLine(gridLeft, (float)y, gridRight, (float)y,
                        EditorPalette.WithAlpha(Palette.MinorSubdivision, Palette.MinorSubdivisionAlpha).ToWinColor(),
                        Palette.MinorSubdivisionWidth);
                    break;
            }
        }
    }

    private void DrawChannelSeparators(CanvasDrawingSession drawing, double left, double width, double playheadY)
    {
        var channelWidth = ChannelWidth(width);
        var gridLeft = (float)(left + Palette.GridInsetX);
        var gridTop = (float)Palette.GridInsetTop;
        var gridBottom = (float)Math.Max(gridTop, playheadY - Palette.GridInsetBottom);
        for (var index = 0; index <= ActiveTracks.Count; index++)
        {
            var x = gridLeft + (float)(index * channelWidth);
            drawing.DrawLine(x, gridTop, x, gridBottom,
                EditorPalette.WithAlpha(Palette.EventColumnSeparator, 95).ToWinColor(), 1f);
        }
    }

    private double ChannelWidth(double width) =>
        Math.Max(1, width - Palette.GridInsetX * 2) / Math.Max(1, ActiveTracks.Count);

    private void DrawHeader(CanvasDrawingSession drawing, double left, double width)
    {
        var gridLeft = (float)(left + Palette.GridInsetX);
        var gridWidth = (float)Math.Max(1, width - Palette.GridInsetX * 2);
        drawing.FillRectangle(new Rect(gridLeft, 0, gridWidth, (float)HeaderHeight),
            Palette.EventHeaderBackground.ToWinColor());
        var format = CanvasExtensions.TextFormat(16f, bold: true);
        var channelWidth = ChannelWidth(width);
        for (var channel = 0; channel < ActiveTracks.Count; channel++)
        {
            drawing.DrawText(ActiveTracks[channel].DisplayName(),
                new Vector2(gridLeft + (float)(channel * channelWidth + 6), 4),
                Palette.EventHeaderText.ToWinColor(), format);
        }
    }

    /// <summary>判定线数值：只显示数字，绿色粗体，中心压在灰线上，绘制在事件条上层。</summary>
    private void DrawEventValues(CanvasDrawingSession drawing, EditorSession session,
        double left, double width, double playheadY)
    {
        var line = session.CurrentLine;
        var beat = session.Viewport.Position;
        var gridLeft = left + Palette.GridInsetX;
        var channelWidth = ChannelWidth(width);
        var format = CanvasExtensions.TextFormat(24f, center: true, bold: true);
        var valueColor = Windows.UI.Color.FromArgb(255, 60, 230, 90);

        for (var channel = 0; channel < ActiveTracks.Count; channel++)
        {
            var kind = ActiveTracks[channel];
            var value = line is null ? 0 : CurrentValue(line, kind, session.CurrentLayer, beat);
            drawing.DrawText(value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                new Vector2((float)(gridLeft + channel * channelWidth + channelWidth / 2),
                    (float)(playheadY - 15)), valueColor, format);
        }
    }

    private static double CurrentValue(JudgeLine line, EventTrackKind kind, int layer, double beat)
    {
        double? value = null;
        foreach (var item in line.Track(kind, layer))
        {
            if (beat >= item.StartBeat.Value && beat <= item.EndBeat.Value && item.DurationBeats > 1e-9)
            {
                var progress = (beat - item.StartBeat.Value) / item.DurationBeats;
                var eased = PhiEdit.Core.Easing.Easing.Evaluate(progress, item.Easing, item.EasingLeft, item.EasingRight);
                return item.StartValue + (item.EndValue - item.StartValue) * eased;
            }

            if (item.EndBeat.Value <= beat + 1e-9)
            {
                value = item.EndValue;
            }
        }

        return value ?? (kind is EventTrackKind.Alpha or EventTrackKind.Speed ? 1 : 0);
    }

    private void DrawPlayhead(CanvasDrawingSession drawing, double left, double width, double playheadY)
    {
        var gridLeft = (float)(left + Palette.GridInsetX);
        var gridRight = (float)Math.Max(gridLeft + 1, left + width - Palette.GridInsetX);
        drawing.DrawLine(gridLeft, (float)playheadY, gridRight, (float)playheadY,
            EditorPalette.WithAlpha(Palette.Judgement, Palette.JudgementAlpha).ToWinColor(), Palette.JudgementWidth);
    }

    private void DrawMarquee(CanvasDrawingSession drawing)
    {
        if (_marqueeArea == MarqueeArea.None)
        {
            return;
        }

        var rect = new Rect(Math.Min(_marqueeStartX, _marqueeCurrentX), Math.Min(_marqueeStartY, _marqueeCurrentY),
            Math.Abs(_marqueeCurrentX - _marqueeStartX), Math.Abs(_marqueeCurrentY - _marqueeStartY));
        drawing.FillRectangle(rect, EditorPalette.WithAlpha(Palette.MarqueeFill, 40).ToWinColor());
        drawing.DrawRectangle(rect, Palette.MarqueeStroke.ToWinColor(), 1f);
    }

    private double NoteScale(double paneWidth) => NoteSize * paneWidth / 1350.0;

    private static double BaseWidth(double scale, Note note) => scale * Math.Abs(note.Size);

    private void DrawNotes(CanvasDrawingSession drawing, EditorSession session, EditViewport viewport,
        double left, double width, double height)
    {
        var line = session.CurrentLine;
        if (line is null)
        {
            return;
        }

        var renderer = Renderer;
        var skin = renderer?.Skin;
        var textured = renderer is not null && renderer.IsReady && skin is not null;
        var scale = NoteScale(width);
        var simultaneous = session.Document.SimultaneousNotes();

        foreach (var note in line.Notes)
        {
            var y = viewport.YAtBeat(note.StartBeat.Value);
            var endY = viewport.YAtBeat(note.EndBeat.Value);
            if (Math.Max(y, endY) < -60 || Math.Min(y, endY) > height + 60)
            {
                continue;
            }

            var x = (float)(left + Palette.GridInsetX + viewport.StageXToPixel(note.PositionX));
            var selected = session.SelectedNotes.Contains(note);
            var highlighted = simultaneous.Contains(note);
            var alpha = (note.IsFake ? 0.45f : 1f) * (float)(Math.Clamp(note.Alpha, 0, 255) / 255);

            if (textured && renderer is not null && skin is not null &&
                DrawTextured(drawing, renderer, skin, note, x, y, endY, scale, highlighted, alpha, selected))
            {
                // 贴图已直接染绿，不再叠加绿色描边；描边只用于无贴图的回退绘制。
                continue;
            }

            var color = Palette.NoteColor(note.Type).ToWinColor();
            var plainWidth = (float)BaseWidth(scale, note);
            var plainHeight = 8f * (float)scale;
            if (note.Type == NoteType.Hold && Math.Abs(endY - y) > 0.5)
            {
                var top = (float)Math.Min(y, endY);
                var bodyHeight = (float)Math.Abs(endY - y);
                drawing.FillRoundedRectangle(new Rect(x - plainWidth / 2, top, plainWidth, bodyHeight), 4, 4,
                    EditorPalette.WithAlpha(Palette.NoteHold, 110).ToWinColor());
                drawing.DrawRoundedRectangle(new Rect(x - plainWidth / 2, top, plainWidth, bodyHeight), 4, 4, color, 1.5f);
            }

            var headY = (float)y - plainHeight / 2;
            drawing.FillRoundedRectangle(new Rect(x - plainWidth / 2, headY, plainWidth, plainHeight), 4, 4, color);
            if (selected)
            {
                var bounds = new Rect(x - plainWidth / 2 - 3, headY - 3, plainWidth + 6, plainHeight + 6);
                drawing.FillRectangle(bounds, EditorPalette.WithAlpha(Palette.SelectionFill, 90).ToWinColor());
                drawing.DrawRectangle(bounds, Palette.Selection.ToWinColor(), 2f);
            }
        }
    }

    private static Rect NoteBounds(NoteSkinRenderer renderer, NoteSkin skin, Note note, float x,
        double y, double endY, double scale, bool highlighted)
    {
        var baseWidth = BaseWidth(scale, note);
        var noteWidth = (float)(baseWidth * (highlighted ? skin.HighlightScale : 1));

        if (note.Type == NoteType.Hold)
        {
            var bitmap = renderer[highlighted ? "hold_mh.png" : "hold.png"];
            if (bitmap is not null)
            {
                var geometryBitmap = highlighted ? renderer["hold.png"] ?? bitmap : bitmap;
                var geometryAtlas = skin.Info.HoldAtlas;
                var textureScale = noteWidth / Math.Max(1, geometryBitmap.Size.Width);
                var headHeight = (float)(geometryAtlas[1] * textureScale);
                var tailHeight = (float)(geometryAtlas[0] * textureScale);
                var top = (float)Math.Min(y, endY);
                var bottom = (float)Math.Max(y, endY);
                return new Rect(x - noteWidth / 2, top, noteWidth, Math.Max(headHeight + tailHeight, bottom - top));
            }
        }

        var texture = renderer[highlighted ? "click_mh.png" : "click.png"];
        var height = noteWidth * (texture?.Size.Height ?? 8) / Math.Max(1, texture?.Size.Width ?? 1);
        return new Rect(x - noteWidth / 2, (float)y - height / 2, noteWidth, height);
    }

    private static bool DrawTextured(CanvasDrawingSession drawing, NoteSkinRenderer renderer, NoteSkin skin,
        Note note, float x, double y, double endY, double scale, bool highlighted, float opacity, bool selected)
    {
        var baseWidth = BaseWidth(scale, note);
        var noteWidth = baseWidth * (highlighted ? skin.HighlightScale : 1);

        if (note.Type == NoteType.Hold)
        {
            return DrawHoldTextured(drawing, renderer, skin, x, y, endY, noteWidth, highlighted, opacity, selected);
        }

        var name = note.Type switch
        {
            NoteType.Drag => highlighted ? "drag_mh.png" : "drag.png",
            NoteType.Flick => highlighted ? "flick_mh.png" : "flick.png",
            _ => highlighted ? "click_mh.png" : "click.png",
        };

        var bitmap = renderer[name];
        if (bitmap is null)
        {
            return false;
        }

        var height = noteWidth * bitmap.Size.Height / Math.Max(1, bitmap.Size.Width);
        DrawNoteImage(drawing, bitmap,
            new Rect(x - (float)noteWidth / 2, (float)y - (float)height / 2, (float)noteWidth, (float)height),
            new Rect(0, 0, bitmap.Size.Width, bitmap.Size.Height), opacity, selected);
        return true;
    }

    /// <summary>把贴图直接染成绿色；ColorMatrix 保留原图 alpha，形状不变。</summary>
    private static void DrawNoteImage(CanvasDrawingSession drawing, Microsoft.Graphics.Canvas.CanvasBitmap bitmap,
        Rect destination, Rect source, float opacity, bool selected)
    {
        if (!selected)
        {
            drawing.DrawImage(bitmap, destination, source, opacity);
            return;
        }

        var matrix = new Matrix5x4
        {
            M11 = 0, M12 = 0, M13 = 0, M14 = 0,
            M21 = 0, M22 = 0, M23 = 0, M24 = 0,
            M31 = 0, M32 = 0, M33 = 0, M34 = 0,
            M41 = 0, M42 = 0, M43 = 0, M44 = 1,
            M51 = 0, M52 = 1, M53 = 0.15f, M54 = 0,
        };
        var effect = new ColorMatrixEffect { Source = bitmap, ColorMatrix = matrix };
        drawing.DrawImage(effect, destination, source, opacity);
    }

    private static bool DrawHoldTextured(CanvasDrawingSession drawing, NoteSkinRenderer renderer, NoteSkin skin,
        float x, double y, double endY, double noteWidth, bool highlighted, float opacity, bool selected)
    {
        var bitmap = renderer[highlighted ? "hold_mh.png" : "hold.png"];
        if (bitmap is null)
        {
            return false;
        }

        var atlas = highlighted ? skin.Info.HoldAtlasMH : skin.Info.HoldAtlas;
        var tailPixels = atlas[0];
        var headPixels = atlas[1];
        var imageHeight = bitmap.Size.Height;
        var bodyPixels = imageHeight - tailPixels - headPixels;
        if (bodyPixels <= 0)
        {
            return false;
        }

        var headY = Math.Max(y, endY);
        var tailY = Math.Min(y, endY);
        var geometryBitmap = highlighted ? renderer["hold.png"] ?? bitmap : bitmap;
        var geometryAtlas = skin.Info.HoldAtlas;
        var textureScale = noteWidth / Math.Max(1, geometryBitmap.Size.Width);
        var headHeight = geometryAtlas[1] * textureScale;
        var tailHeight = geometryAtlas[0] * textureScale;
        var half = (float)noteWidth / 2;

        var bodyTop = tailY + tailHeight;
        var bodyBottom = headY - headHeight;
        if (bodyBottom > bodyTop)
        {
            DrawNoteImage(drawing, bitmap,
                new Rect(x - half, (float)bodyTop, (float)noteWidth, (float)(bodyBottom - bodyTop)),
                new Rect(0, tailPixels, bitmap.Size.Width, bodyPixels), opacity, selected);
        }

        DrawNoteImage(drawing, bitmap,
            new Rect(x - half, (float)(headY - headHeight), (float)noteWidth, (float)headHeight),
            new Rect(0, imageHeight - headPixels, bitmap.Size.Width, headPixels), opacity, selected);

        if (tailHeight > 0)
        {
            DrawNoteImage(drawing, bitmap,
                new Rect(x - half, (float)tailY, (float)noteWidth, (float)tailHeight),
                new Rect(0, 0, bitmap.Size.Width, tailPixels), opacity, selected);
        }

        return true;
    }

    private void DrawEvents(CanvasDrawingSession drawing, EditorSession session, EditViewport viewport,
        double left, double width, double height)
    {
        var line = session.CurrentLine;
        if (line is null)
        {
            return;
        }

        var channelWidth = ChannelWidth(width);
        var barWidth = Math.Max(6, channelWidth * 0.62);
        var gridLeft = left + Palette.GridInsetX;

        for (var channel = 0; channel < ActiveTracks.Count; channel++)
        {
            var kind = ActiveTracks[channel];
            var events = line.Track(kind, session.CurrentLayer);
            if (events.Count == 0)
            {
                continue;
            }

            var barX = (float)(gridLeft + channel * channelWidth + (channelWidth - barWidth) / 2);
            var visible = events.Where(item =>
            {
                var top = viewport.YAtBeat(item.EndBeat.Value);
                var bottom = viewport.YAtBeat(item.StartBeat.Value);
                return bottom >= -10 && top <= height + 10;
            }).ToList();
            var values = visible.SelectMany(item => new[] { item.StartValue, item.EndValue }).ToList();
            var rangeMin = values.Count > 0 ? values.Min() : 0;
            var rangeMax = values.Count > 0 ? values.Max() : 1;
            if (Math.Abs(rangeMax - rangeMin) < 1e-9)
            {
                rangeMin -= 1;
                rangeMax += 1;
            }

            foreach (var item in visible)
            {
                var top = viewport.YAtBeat(item.EndBeat.Value);
                var bottom = viewport.YAtBeat(item.StartBeat.Value);
                var rectTop = (float)Math.Max(0, top);
                var rectBottom = (float)Math.Min(height, bottom);
                var rectHeight = Math.Max(2f, rectBottom - rectTop);
                var selected = session.SelectedEvents.Contains(item);
                var fillTop = selected
                    ? Windows.UI.Color.FromArgb(220, 76, 232, 108)
                    : Windows.UI.Color.FromArgb(210, 112, 231, 226);
                var fillBottom = selected
                    ? Windows.UI.Color.FromArgb(220, 24, 196, 72)
                    : Windows.UI.Color.FromArgb(210, 236, 182, 218);
                using var gradient = new CanvasLinearGradientBrush(drawing, fillTop, fillBottom)
                {
                    StartPoint = new Vector2((float)(barX + barWidth / 2), rectTop),
                    EndPoint = new Vector2((float)(barX + barWidth / 2), rectBottom),
                };
                drawing.FillRectangle(new Rect(barX, rectTop, (float)barWidth, rectHeight), gradient);

                // 事件条中央白线与加粗的橙色缓动曲线。
                var centerX = barX + (float)barWidth / 2;
                drawing.DrawLine(centerX, rectTop, centerX, rectBottom,
                    Windows.UI.Color.FromArgb(190, 255, 255, 255), 1f);
                DrawEasingCurve(drawing, item, barX, (float)barWidth, rectTop, rectBottom, rangeMin, rangeMax);
            }
        }
    }

    private void DrawEasingCurve(CanvasDrawingSession drawing, LineEvent item, float barX, float barWidth,
        float top, float bottom, double rangeMin, double rangeMax)
    {
        if (item.DurationBeats <= 1e-9 || bottom - top <= 2f)
        {
            return;
        }

        const int steps = 36;
        const float padding = 4f;
        var usableWidth = Math.Max(1f, barWidth - padding * 2);
        var color = EditorPalette.WithAlpha(Palette.EventCurve, 240).ToWinColor();
        Vector2? previous = null;

        for (var step = 0; step <= steps; step++)
        {
            var progress = step / (double)steps;
            var eased = PhiEdit.Core.Easing.Easing.Evaluate(progress, item.Easing, item.EasingLeft, item.EasingRight);
            var value = item.StartValue + (item.EndValue - item.StartValue) * eased;
            var normalized = Math.Clamp((value - rangeMin) / (rangeMax - rangeMin), 0, 1);
            var point = new Vector2(barX + padding + (float)normalized * usableWidth,
                bottom - (float)progress * (bottom - top));
            if (previous is Vector2 last)
            {
                drawing.DrawLine(last, point, color, Palette.BeatLineWidth);
            }

            previous = point;
        }
    }

    // ---------------------------------------------------------------- 交互

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(_canvas).Position;
        _lastPointerPosition = point;
        var layout = Layout(_canvas.ActualWidth);
        _pressX = point.X;
        _pressY = point.Y;

        if (point.X <= layout.LeftWidth)
        {
            BeginNoteInteraction(point, e);
        }
        else if (point.X >= layout.RightLeft)
        {
            BeginEventInteraction(point, e, layout);
        }

        e.Handled = true;
    }

    private void BeginNoteInteraction(Point point, PointerRoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        var ctrl = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift);
        var hit = HitTestNote(point.X, point.Y);

        if (hit is not null)
        {
            if (ctrl)
            {
                _session.SelectedEvents.Clear();
                if (!_session.SelectedNotes.Remove(hit))
                {
                    _session.SelectedNotes.Add(hit);
                }
                RaiseSelectionChanged();
                return;
            }

            if (_session.SelectedNotes.Contains(hit))
            {
                _session.SelectedEvents.Clear();
            }
            else
            {
                _session.SelectNote(hit);
            }

            RaiseSelectionChanged();
            _dragNote = hit;
            _noteDragMoved = false;
            CaptureNoteOrigins();
            _canvas.CapturePointer(e.Pointer);
            return;
        }

        _marqueeArea = MarqueeArea.Notes;
        _marqueeAppend = shift;
        _marqueeStartX = _marqueeCurrentX = point.X;
        _marqueeStartY = _marqueeCurrentY = point.Y;
        if (!shift)
        {
            _session.SelectedNotes.Clear();
            _session.SelectedEvents.Clear();
            RaiseSelectionChanged();
        }

        _canvas.CapturePointer(e.Pointer);
    }

    private void BeginEventInteraction(Point point, PointerRoutedEventArgs e,
        (double Split, double LeftWidth, double RightLeft, double RightWidth) layout)
    {
        if (_session is null)
        {
            return;
        }

        var ctrl = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift);
        var hit = HitTestEvent(point.X - layout.RightLeft, point.Y, layout.RightWidth);

        if (hit is not null)
        {
            if (ctrl)
            {
                _session.SelectedNotes.Clear();
                if (!_session.SelectedEvents.Remove(hit))
                {
                    _session.SelectedEvents.Add(hit);
                }
                RaiseSelectionChanged();
                return;
            }

            if (_session.SelectedEvents.Contains(hit))
            {
                _session.SelectedNotes.Clear();
            }
            else
            {
                _session.SelectEvent(hit);
            }

            RaiseSelectionChanged();
            _dragEvent = hit;
            _eventDragMoved = false;
            CaptureEventOrigins();
            _canvas.CapturePointer(e.Pointer);
            return;
        }

        _marqueeArea = MarqueeArea.Events;
        _marqueeAppend = shift;
        _marqueeStartX = _marqueeCurrentX = point.X;
        _marqueeStartY = _marqueeCurrentY = point.Y;
        if (!shift)
        {
            _session.SelectedEvents.Clear();
            _session.SelectedNotes.Clear();
            RaiseSelectionChanged();
        }

        _canvas.CapturePointer(e.Pointer);
    }

    private void CaptureNoteOrigins()
    {
        _noteOrigins.Clear();
        foreach (var note in _session!.SelectedNotes)
        {
            _noteOrigins.Add(new NoteDragOrigin(note, note.StartBeat.Value, note.EndBeat.Value, note.PositionX));
        }
    }

    private void CaptureEventOrigins()
    {
        _eventOrigins.Clear();
        foreach (var item in _session!.SelectedEvents)
        {
            _eventOrigins.Add(new EventDragOrigin(item, item.StartBeat.Value, item.EndBeat.Value,
                item.StartValue, item.EndValue));
        }
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(_canvas);
        _lastPointerPosition = point.Position;
        if (_holdPending || _eventPending)
        {
            Invalidate();
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        var position = point.Position;
        if (_marqueeArea != MarqueeArea.None)
        {
            _marqueeCurrentX = position.X;
            _marqueeCurrentY = position.Y;
            Invalidate();
            e.Handled = true;
            return;
        }

        var viewport = _session.Viewport;
        var beat = SnapBeat(viewport.BeatAtY(position.Y));
        var leftWidth = Layout(_canvas.ActualWidth).LeftWidth;

        if (_dragNote is not null)
        {
            if (!_noteDragMoved && Math.Abs(position.X - _pressX) < DragThreshold &&
                Math.Abs(position.Y - _pressY) < DragThreshold)
            {
                return;
            }

            var clampedX = ClampNotePixel(position.X, leftWidth);
            var stageX = SnapX(viewport.PixelToStageX(clampedX - Palette.GridInsetX));
            var origin = _noteOrigins[0];
            var deltaBeat = beat - origin.Beat;
            var deltaX = stageX - origin.X;
            foreach (var state in _noteOrigins)
            {
                state.Note.StartBeat = Beat.FromValue(state.Beat + deltaBeat);
                state.Note.EndBeat = Beat.FromValue(state.End + deltaBeat);
                state.Note.PositionX = state.X + deltaX;
            }

            _noteDragMoved = true;
            Invalidate();
            e.Handled = true;
            return;
        }

        if (_dragEvent is not null)
        {
            if (!_eventDragMoved && Math.Abs(position.X - _pressX) < DragThreshold &&
                Math.Abs(position.Y - _pressY) < DragThreshold)
            {
                return;
            }

            var delta = beat - _eventOrigins[0].Start;
            foreach (var state in _eventOrigins)
            {
                state.Event.StartBeat = Beat.FromValue(state.Start + delta);
                state.Event.EndBeat = Beat.FromValue(state.End + delta);
            }

            _eventDragMoved = true;
            Invalidate();
            e.Handled = true;
        }
    }

    private double SnapBeat(double beat)
    {
        if (_session!.SnapTime)
        {
            beat = Math.Round(beat * _session.Division, MidpointRounding.AwayFromZero) / _session.Division;
        }

        return Math.Max(0, beat);
    }

    private double SnapX(double stageX) => _session!.SnapX ? _session.Grid.Snap(stageX) : stageX;

    /// <summary>音符中心不得越过音符区左右两条封闭黄线。</summary>
    private double ClampNotePixel(double x, double leftWidth)
    {
        var first = Palette.GridInsetX;
        var last = Math.Max(first + 1, leftWidth - Palette.GridInsetX);
        return Math.Clamp(x, first, last);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndDrag();
        e.Handled = true;
    }

    private void EndDrag()
    {
        if (_marqueeArea != MarqueeArea.None)
        {
            ApplyMarquee();
            _marqueeArea = MarqueeArea.None;
            Invalidate();
            return;
        }

        if (_dragNote is not null)
        {
            var note = _dragNote;
            _dragNote = null;
            if (!_noteDragMoved || _session is null || _noteOrigins.Count == 0)
            {
                return;
            }

            var finals = _noteOrigins.Select(state =>
                new NoteDragOrigin(state.Note, state.Note.StartBeat.Value, state.Note.EndBeat.Value, state.Note.PositionX)).ToList();
            var origins = _noteOrigins.ToList();
            ApplyNoteStates(origins);
            _editor?.Undo.Push(new DelegateAction($"移动 {origins.Count} 个音符",
                () => ApplyNoteStates(finals), () => ApplyNoteStates(origins)));
            _session.Document.IsDirty = true;
            RaiseSelectionChanged();
            return;
        }

        if (_dragEvent is not null)
        {
            var item = _dragEvent;
            _dragEvent = null;
            if (!_eventDragMoved || _session is null || _eventOrigins.Count == 0)
            {
                return;
            }

            var finals = _eventOrigins.Select(state =>
                new EventDragOrigin(state.Event, state.Event.StartBeat.Value, state.Event.EndBeat.Value,
                    state.StartValue, state.EndValue)).ToList();
            var origins = _eventOrigins.ToList();
            ApplyEventStates(origins);
            _editor?.Undo.Push(new DelegateAction($"移动 {origins.Count} 个事件",
                () => ApplyEventStates(finals), () => ApplyEventStates(origins)));
            _session.Document.IsDirty = true;
            RaiseSelectionChanged();
        }
    }

    private static void ApplyNoteStates(IEnumerable<NoteDragOrigin> states)
    {
        foreach (var state in states)
        {
            state.Note.StartBeat = Beat.FromValue(state.Beat);
            state.Note.EndBeat = Beat.FromValue(state.End);
            state.Note.PositionX = state.X;
        }
    }

    private static void ApplyEventStates(IEnumerable<EventDragOrigin> states)
    {
        foreach (var state in states)
        {
            state.Event.StartBeat = Beat.FromValue(state.Start);
            state.Event.EndBeat = Beat.FromValue(state.End);
        }
    }

    private void ApplyMarquee()
    {
        if (_session is null || _session.CurrentLine is null)
        {
            return;
        }

        var viewport = _session.Viewport;
        var left = Math.Min(_marqueeStartX, _marqueeCurrentX);
        var right = Math.Max(_marqueeStartX, _marqueeCurrentX);
        var top = Math.Min(_marqueeStartY, _marqueeCurrentY);
        var bottom = Math.Max(_marqueeStartY, _marqueeCurrentY);

        if (_marqueeArea == MarqueeArea.Notes)
        {
            if (!_marqueeAppend)
            {
                _session.SelectedNotes.Clear();
            }

            foreach (var note in _session.CurrentLine.Notes)
            {
                var x = viewport.StageXToPixel(note.PositionX);
                var y = viewport.YAtBeat(note.StartBeat.Value);
                if (x >= left && x <= right && y >= top && y <= bottom)
                {
                    _session.SelectedNotes.Add(note);
                }
            }
        }
        else if (_marqueeArea == MarqueeArea.Events)
        {
            if (!_marqueeAppend)
            {
                _session.SelectedEvents.Clear();
            }

            var layout = Layout(_canvas.ActualWidth);
            var channelWidth = ChannelWidth(layout.RightWidth);
            var barWidth = Math.Max(6, channelWidth * 0.62);
            for (var channel = 0; channel < ActiveTracks.Count; channel++)
            {
                var barX = layout.RightLeft + Palette.GridInsetX + channel * channelWidth + (channelWidth - barWidth) / 2;
                if (barX + barWidth < left || barX > right)
                {
                    continue;
                }

                foreach (var item in _session.CurrentLine.Track(ActiveTracks[channel], _session.CurrentLayer))
                {
                    var eventTop = viewport.YAtBeat(item.EndBeat.Value);
                    var eventBottom = viewport.YAtBeat(item.StartBeat.Value);
                    if (eventBottom >= top && eventTop <= bottom)
                    {
                        _session.SelectedEvents.Add(item);
                    }
                }
            }
        }

        RaiseSelectionChanged();
    }

    /// <summary>Q/W/E：在当前指针位置放置瞬时音符。</summary>
    public string PlaceNoteAtPointer(NoteType type)
    {
        if (_session?.CurrentLine is null || _editor is null)
        {
            return "没有可编辑的判定线";
        }

        var leftWidth = Layout(_canvas.ActualWidth).LeftWidth;
        if (_lastPointerPosition.X > leftWidth)
        {
            return "指针不在音符区";
        }

        var viewport = _session.Viewport;
        var beat = SnapBeat(viewport.BeatAtY(_lastPointerPosition.Y));
        var stageX = SnapX(viewport.PixelToStageX(ClampNotePixel(_lastPointerPosition.X, leftWidth) - Palette.GridInsetX));
        var note = _editor.PlaceNote(_session.CurrentLine, type, Beat.FromValue(beat), stageX);
        _session.SelectedNotes.Clear();
        _session.SelectedNotes.Add(note);
        RaiseSelectionChanged();
        return $"已放置 {type}";
    }

    /// <summary>R：音符区两段式放置 Hold，判定线区两段式放置事件条。</summary>
    public string HandleRKey()
    {
        if (_session?.CurrentLine is null || _editor is null)
        {
            return "没有可编辑的判定线";
        }

        var layout = Layout(_canvas.ActualWidth);
        var viewport = _session.Viewport;
        var beat = SnapBeat(viewport.BeatAtY(_lastPointerPosition.Y));

        if (_lastPointerPosition.X <= layout.LeftWidth)
        {
            if (!_holdPending)
            {
                _holdPending = true;
                _holdStartBeat = beat;
                _holdStageX = SnapX(viewport.PixelToStageX(
                    ClampNotePixel(_lastPointerPosition.X, layout.LeftWidth) - Palette.GridInsetX));
                Invalidate();
                return "Hold 起点已设置，移动鼠标后再次按 R 确定终点";
            }

            if (beat <= _holdStartBeat + 1e-9)
            {
                return "Hold 终点必须晚于起点";
            }

            var note = _editor.PlaceNote(_session.CurrentLine, NoteType.Hold,
                Beat.FromValue(_holdStartBeat), _holdStageX);
            note.EndBeat = Beat.FromValue(beat);
            _session.Document.IsDirty = true;
            _holdPending = false;
            _session.SelectedNotes.Clear();
            _session.SelectedNotes.Add(note);
            RaiseSelectionChanged();
            return "已放置 Hold";
        }

        if (_lastPointerPosition.X < layout.RightLeft)
        {
            return "指针不在编辑区内";
        }

        var channelWidth = ChannelWidth(layout.RightWidth);
        var gridLeft = layout.RightLeft + Palette.GridInsetX;
        var channel = (int)((_lastPointerPosition.X - gridLeft) / Math.Max(1, channelWidth));
        if (channel < 0 || channel >= ActiveTracks.Count)
        {
            return "指针不在事件轨道内";
        }

        if (!_eventPending)
        {
            _eventPending = true;
            _eventStartBeat = beat;
            _eventChannel = channel;
            Invalidate();
            return "事件起点已设置，移动鼠标后再次按 R 确定终点";
        }

        if (beat <= _eventStartBeat + 1e-9)
        {
            return "事件终点必须晚于起点";
        }

        var kind = ActiveTracks[channel];
        var value = CurrentValue(_session.CurrentLine, kind, _session.CurrentLayer, _eventStartBeat);
        var item = _editor.AddEvent(_session.CurrentLine, kind, _session.CurrentLayer,
            Beat.FromValue(_eventStartBeat), value, value, Beat.FromValue(beat));
        _eventPending = false;
        _session.SelectedEvents.Clear();
        _session.SelectedEvents.Add(item);
        RaiseSelectionChanged();
        return $"已放置 {kind.DisplayName()}事件";
    }

    public void CancelPendingPlacement()
    {
        _holdPending = false;
        _eventPending = false;
        Invalidate();
    }

    private void DrawPendingHold(CanvasDrawingSession drawing, EditorSession session, EditViewport viewport,
        double left, double width, double height)
    {
        if (!_holdPending)
        {
            return;
        }

        var gridLeft = left + Palette.GridInsetX;
        var x = (float)(gridLeft + viewport.StageXToPixel(_holdStageX));
        var startY = viewport.YAtBeat(_holdStartBeat);
        var endY = viewport.YAtBeat(SnapBeat(viewport.BeatAtY(_lastPointerPosition.Y)));
        var top = (float)Math.Max(-10, Math.Min(startY, endY));
        var bottom = (float)Math.Min(height + 10, Math.Max(startY, endY));
        var noteWidth = (float)BaseWidth(NoteScale(width), new Note { Size = 1 });
        drawing.FillRoundedRectangle(new Rect(x - noteWidth / 2, top, noteWidth, Math.Max(2, bottom - top)),
            4, 4, Windows.UI.Color.FromArgb(120, 60, 230, 90));
    }

    private void DrawPendingEvent(CanvasDrawingSession drawing, EditorSession session, EditViewport viewport,
        double left, double width, double height)
    {
        if (!_eventPending || _eventChannel < 0 || _eventChannel >= ActiveTracks.Count)
        {
            return;
        }

        var channelWidth = ChannelWidth(width);
        var barWidth = Math.Max(6, channelWidth * 0.62);
        var barX = (float)(left + Palette.GridInsetX + _eventChannel * channelWidth + (channelWidth - barWidth) / 2);
        var startY = viewport.YAtBeat(_eventStartBeat);
        var endY = viewport.YAtBeat(SnapBeat(viewport.BeatAtY(_lastPointerPosition.Y)));
        var top = (float)Math.Max(-10, Math.Min(startY, endY));
        var bottom = (float)Math.Min(height + 10, Math.Max(startY, endY));
        drawing.FillRectangle(new Rect(barX, top, (float)barWidth, Math.Max(2, bottom - top)),
            Windows.UI.Color.FromArgb(110, 255, 140, 24));
    }

    private void PlaceNote(double x, double y)
    {
        if (_session?.CurrentLine is null || _editor is null)
        {
            return;
        }

        var beat = SnapBeat(_session.Viewport.BeatAtY(y));
        var clampedX = ClampNotePixel(x, Layout(_canvas.ActualWidth).LeftWidth);
        var stageX = SnapX(_session.Viewport.PixelToStageX(clampedX - Palette.GridInsetX));
        var note = _editor.PlaceNote(_session.CurrentLine, _session.Tool!.Value, Beat.FromValue(beat), stageX);
        _session.SelectedNotes.Clear();
        _session.SelectedNotes.Add(note);
        RaiseSelectionChanged();
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_session?.CurrentLine is null || _editor is null)
        {
            return;
        }

        var position = e.GetPosition(_canvas);
        var layout = Layout(_canvas.ActualWidth);
        if (position.X < layout.RightLeft)
        {
            return;
        }

        var channelWidth = ChannelWidth(layout.RightWidth);
        var gridLeft = layout.RightLeft + Palette.GridInsetX;
        var channel = (int)((position.X - gridLeft) / Math.Max(1, channelWidth));
        if (channel < 0 || channel >= ActiveTracks.Count)
        {
            return;
        }

        var beat = SnapBeat(_session.Viewport.BeatAtY(position.Y));
        var kind = ActiveTracks[channel];
        var current = CurrentValue(_session.CurrentLine, kind, _session.CurrentLayer, beat);
        var item = _editor.AddEvent(_session.CurrentLine, kind, _session.CurrentLayer, Beat.FromValue(beat), current);
        _session.SelectedEvents.Clear();
        _session.SelectedEvents.Add(item);
        RaiseSelectionChanged();
    }

    private Note? HitTestNote(double x, double y)
    {
        if (_session?.CurrentLine is null)
        {
            return null;
        }

        var viewport = _session.Viewport;
        var scale = NoteScale(Layout(_canvas.ActualWidth).LeftWidth);
        foreach (var note in _session.CurrentLine.Notes)
        {
            var noteY = viewport.YAtBeat(note.StartBeat.Value);
            var endY = viewport.YAtBeat(note.EndBeat.Value);
            var noteX = Palette.GridInsetX + viewport.StageXToPixel(note.PositionX);
            var noteWidth = BaseWidth(scale, note);
            if (Math.Abs(noteX - x) > noteWidth / 2 + 2)
            {
                continue;
            }

            if (note.Type == NoteType.Hold)
            {
                if (y >= Math.Min(noteY, endY) - 6 && y <= Math.Max(noteY, endY) + 6)
                {
                    return note;
                }
            }
            else if (Math.Abs(noteY - y) <= 9)
            {
                return note;
            }
        }

        return null;
    }

    private LineEvent? HitTestEvent(double x, double y, double width)
    {
        if (_session?.CurrentLine is null)
        {
            return null;
        }

        var viewport = _session.Viewport;
        var channelWidth = ChannelWidth(width);
        var barWidth = Math.Max(6, channelWidth * 0.62);
        var gridLeft = Palette.GridInsetX;
        for (var channel = 0; channel < ActiveTracks.Count; channel++)
        {
            var barX = gridLeft + channel * channelWidth + (channelWidth - barWidth) / 2;
            if (x < barX - 4 || x > barX + barWidth + 4)
            {
                continue;
            }

            foreach (var item in _session.CurrentLine.Track(ActiveTracks[channel], _session.CurrentLayer))
            {
                var top = viewport.YAtBeat(item.EndBeat.Value);
                var bottom = viewport.YAtBeat(item.StartBeat.Value);
                if (y >= Math.Min(top, bottom) - 4 && y <= Math.Max(top, bottom) + 4)
                {
                    return item;
                }
            }
        }

        return null;
    }

    private void RaiseSelectionChanged()
    {
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }
}

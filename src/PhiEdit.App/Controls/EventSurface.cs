using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PhiEdit.Core.Beats;
using PhiEdit.Core.Charts;
using PhiEdit.Core.Editing;
using PhiEdit.Core.Layout;
using PhiEdit.Core.Rendering;
using System.Globalization;
using System.Numerics;
using Windows.Foundation;

namespace PhiEdit.App.Controls;

/// <summary>
/// 判定线区：控制当前判定线的行为（X / Y / 旋转 / 透明度 / 速度 等事件轨道）。
///
/// 与音符区共用同一时间轴与视口，因此两侧始终同步滚动；判定线（播放头）同样固定在画布底部，
/// 网格线只绘制在判定线以上。
/// </summary>
public sealed class EventSurface : UserControl
{
    private const double HeaderHeight = 24;
    private const double FooterHeight = 26;

    /// <summary>触发拖动所需的最小位移（像素）。</summary>
    private const double DragThreshold = 3;

    private readonly CanvasControl _canvas = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
    };

    private EditorSession? _session;
    private ChartEditor? _editor;

    private LineEvent? _dragEvent;
    private double _pressX;
    private double _pressY;
    private bool _dragMoved;

    /// <summary>拖动开始时整组选中项的原始状态。</summary>
    private readonly List<DragOrigin> _dragOrigins = new();

    private readonly record struct DragOrigin(LineEvent Event, EventTrackKind Kind, double Start, double End, double StartValue, double EndValue);

    private bool _marquee;
    private double _marqueeStartX;
    private double _marqueeStartY;
    private double _marqueeCurrentX;
    private double _marqueeCurrentY;
    private bool _marqueeAppend;

    public EventSurface()
    {
        Content = _canvas;
        _canvas.Draw += OnDraw;
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

    public EditorSession? Session
    {
        get => _session;
        set
        {
            if (_session is not null)
            {
                _session.Viewport.ViewChanged -= OnViewportViewChanged;
            }

            _session = value;

            if (_session is not null)
            {
                // 纵向位置是唯一真源：视口一变化，本区立即重绘。
                _session.Viewport.ViewChanged += OnViewportViewChanged;
            }

            Invalidate();
        }
    }

    private void OnViewportViewChanged(object? sender, EventArgs e) => Invalidate();

    public ChartEditor? Editor
    {
        get => _editor;
        set => _editor = value;
    }

    public event EventHandler? SelectionChanged;

    /// <summary>当前显示的事件轨道集合。</summary>
    public IReadOnlyList<EventTrackKind> Tracks { get; set; } = EventTrackKinds.Standard;

    /// <summary>请求重绘。</summary>
    public void Invalidate() => _canvas.Invalidate();

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var session = _session;
        var drawing = args.DrawingSession;
        drawing.Clear(Palette.SurfaceAlt.ToWinColor());

        if (session is null)
        {
            return;
        }

        var width = _canvas.ActualWidth;
        var height = _canvas.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var viewport = session.Viewport;
        viewport.ViewportHeight = height;

        var playheadY = viewport.PlayheadY;
        var channelWidth = width / Math.Max(1, Tracks.Count);
        var barWidth = Math.Max(6, channelWidth * 0.62);

        DrawTimeGrid(drawing, session, viewport, width, playheadY);
        DrawChannelSeparators(drawing, width, playheadY, channelWidth);
        DrawEvents(drawing, session, viewport, channelWidth, barWidth, width, height);
        DrawHeader(drawing, channelWidth, width);
        DrawFooter(drawing, session, channelWidth, width, height);
        DrawMarquee(drawing);
        DrawPlayhead(drawing, width, playheadY);
    }

    private void DrawTimeGrid(CanvasDrawingSession drawing, EditorSession session, EditViewport viewport,
        double width, double playheadY)
    {
        var first = viewport.BottomBeat - 1;
        var last = viewport.TopBeat + 1;

        foreach (var tick in GridLayout.Ticks(first, last, session.Division))
        {
            if (tick.Beat < 0)
            {
                continue;
            }

            var y = viewport.YAtBeat(tick.Beat);
            if (y < -4 || y > playheadY + 4)
            {
                continue;
            }

            switch (tick.Kind)
            {
                case GridTickKind.Beat:
                    drawing.DrawLine(0, (float)y, (float)width, (float)y, Palette.BeatLine.ToWinColor(), Palette.BeatLineWidth);
                    var format = CanvasExtensions.TextFormat(12f, right: true);
                    drawing.DrawText(tick.Beat.ToString("0.##", CultureInfo.InvariantCulture),
                        new Vector2((float)(width - 8), (float)(y - 16)), Palette.BeatLabel.ToWinColor(), format);
                    break;
                case GridTickKind.Subdivision:
                    drawing.DrawLine(0, (float)y, (float)width, (float)y,
                        EditorPalette.WithAlpha(Palette.Subdivision, Palette.SubdivisionAlpha).ToWinColor(), 1f);
                    break;
                default:
                    drawing.DrawLine(0, (float)y, (float)width, (float)y,
                        EditorPalette.WithAlpha(Palette.MinorSubdivision, Palette.MinorSubdivisionAlpha).ToWinColor(), 1f);
                    break;
            }
        }
    }

    private void DrawChannelSeparators(CanvasDrawingSession drawing, double width, double playheadY, double channelWidth)
    {
        var color = EditorPalette.WithAlpha(Palette.EventColumnSeparator, 95).ToWinColor();
        for (var index = 0; index <= Tracks.Count; index++)
        {
            var x = (float)(index * channelWidth);
            drawing.DrawLine(x, 0, x, (float)playheadY, color, 1f);
        }
    }

    private void DrawEvents(CanvasDrawingSession drawing, EditorSession session, EditViewport viewport,
        double channelWidth, double barWidth, double width, double height)
    {
        var line = session.CurrentLine;
        if (line is null)
        {
            return;
        }

        for (var channel = 0; channel < Tracks.Count; channel++)
        {
            var kind = Tracks[channel];
            var events = line.Track(kind, session.CurrentLayer);
            if (events.Count == 0)
            {
                continue;
            }

            var barX = (float)(channel * channelWidth + (channelWidth - barWidth) / 2);

            foreach (var item in events)
            {
                var top = viewport.YAtBeat(item.EndBeat.Value);
                var bottom = viewport.YAtBeat(item.StartBeat.Value);
                if (bottom < -10 || top > height + 10)
                {
                    continue;
                }

                var rectTop = (float)Math.Max(0, top);
                var rectBottom = (float)Math.Min(height, bottom);
                var rectHeight = Math.Max(2f, rectBottom - rectTop);
                var selected = session.SelectedEvents.Contains(item);

                var fill = selected ? Palette.EventFillSelected : Palette.EventFill;
                var stroke = selected ? Palette.EventStrokeSelected : Palette.EventStroke;
                drawing.FillRectangle(new Rect(barX, rectTop, (float)barWidth, rectHeight),
                    EditorPalette.WithAlpha(fill, 64).ToWinColor());
                drawing.DrawRectangle(new Rect(barX, rectTop, (float)barWidth, rectHeight), stroke.ToWinColor(),
                    selected ? 2f : 1f);
            }
        }
    }

    private void DrawHeader(CanvasDrawingSession drawing, double channelWidth, double width)
    {
        drawing.FillRectangle(new Rect(0, 0, (float)width, (float)HeaderHeight), Palette.EventHeaderBackground.ToWinColor());
        var format = CanvasExtensions.TextFormat(12f);
        for (var channel = 0; channel < Tracks.Count; channel++)
        {
            var x = (float)(channel * channelWidth + 6);
            drawing.DrawText(Tracks[channel].DisplayName(), new Vector2(x, 4), Palette.EventHeaderText.ToWinColor(), format);
        }
    }

    /// <summary>底栏：显示各轨道在判定线处的当前数值。</summary>
    private void DrawFooter(CanvasDrawingSession drawing, EditorSession session,
        double channelWidth, double width, double height)
    {
        var top = (float)(height - FooterHeight);
        drawing.FillRectangle(new Rect(0, top, (float)width, (float)FooterHeight), Palette.EventHeaderBackground.ToWinColor());

        var line = session.CurrentLine;
        var beat = session.Viewport.Position;
        var format = CanvasExtensions.TextFormat(13f, center: true);
        for (var channel = 0; channel < Tracks.Count; channel++)
        {
            var kind = Tracks[channel];
            var value = line is null ? 0 : CurrentValue(line, kind, session.CurrentLayer, beat);
            var text = kind.FormatValue(value);
            var color = value < 0 ? Palette.EventValueBottom : Palette.EventValueTop;
            drawing.DrawText(text,
                new Vector2((float)(channel * channelWidth + channelWidth / 2), top + 5),
                color.ToWinColor(), format);
        }
    }

    /// <summary>框选区域（橡皮筋）。</summary>
    private void DrawMarquee(CanvasDrawingSession drawing)
    {
        if (!_marquee)
        {
            return;
        }

        var left = Math.Min(_marqueeStartX, _marqueeCurrentX);
        var top = Math.Min(_marqueeStartY, _marqueeCurrentY);
        var rect = new Rect(left, top, Math.Abs(_marqueeCurrentX - _marqueeStartX), Math.Abs(_marqueeCurrentY - _marqueeStartY));
        drawing.FillRectangle(rect, EditorPalette.WithAlpha(Palette.MarqueeFill, 40).ToWinColor());
        drawing.DrawRectangle(rect, Palette.MarqueeStroke.ToWinColor(), 1f);
    }

    /// <summary>判定线（播放头）：固定在画布底部的粗灰线，压在所有内容之上。</summary>
    private void DrawPlayhead(CanvasDrawingSession drawing, double width, double playheadY)
    {
        var color = EditorPalette.WithAlpha(Palette.Judgement, Palette.JudgementAlpha).ToWinColor();
        drawing.DrawLine(0, (float)playheadY, (float)width, (float)playheadY, color, Palette.JudgementWidth);
    }

    /// <summary>取某轨道在指定拍的当前数值（取该拍之前最后一条事件的结束值）。</summary>
    public static double CurrentValue(JudgeLine line, EventTrackKind kind, int layer, double beat)
    {
        var events = line.Track(kind, layer);
        double? value = null;
        foreach (var item in events)
        {
            if (item.EndBeat.Value <= beat + 1e-9)
            {
                value = item.EndValue;
            }
        }

        if (value is null)
        {
            return kind switch
            {
                EventTrackKind.Alpha => 1,
                EventTrackKind.Speed => 1,
                _ => 0,
            };
        }

        return value.Value;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(_canvas);
        var position = point.Position;
        _pressX = position.X;
        _pressY = position.Y;

        var ctrl = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift);
        var hit = HitTestEvent(position.X, position.Y);

        if (hit is not null)
        {
            if (ctrl)
            {
                if (!_session.SelectedEvents.Remove(hit))
                {
                    _session.SelectedEvents.Add(hit);
                }

                SelectionChanged?.Invoke(this, EventArgs.Empty);
                Invalidate();
                e.Handled = true;
                return;
            }

            if (!_session.SelectedEvents.Contains(hit))
            {
                _session.SelectedEvents.Clear();
                _session.SelectedEvents.Add(hit);
            }

            _dragEvent = hit;
            _dragMoved = false;
            CaptureDragOrigins();
            _canvas.CapturePointer(e.Pointer);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            Invalidate();
            e.Handled = true;
        }
        else
        {
            // 空白处：框选（Shift 为追加模式）。
            _marquee = true;
            _marqueeAppend = shift;
            _marqueeStartX = _marqueeCurrentX = position.X;
            _marqueeStartY = _marqueeCurrentY = position.Y;
            if (!shift)
            {
                _session.SelectedEvents.Clear();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }

            _canvas.CapturePointer(e.Pointer);
            Invalidate();
            e.Handled = true;
        }
    }

    private void CaptureDragOrigins()
    {
        _dragOrigins.Clear();
        foreach (var item in _session.SelectedEvents)
        {
            _dragOrigins.Add(new DragOrigin(item, item.Kind, item.StartBeat.Value, item.EndBeat.Value, item.StartValue, item.EndValue));
        }
    }

    private static void ApplyStates(IEnumerable<DragOrigin> states)
    {
        foreach (var state in states)
        {
            state.Event.StartBeat = Beat.FromValue(state.Start);
            state.Event.EndBeat = Beat.FromValue(state.End);
        }
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_session?.CurrentLine is null || _editor is null)
        {
            return;
        }

        var position = e.GetPosition(_canvas);
        var channelWidth = _canvas.ActualWidth / Math.Max(1, Tracks.Count);
        var channel = (int)(position.X / Math.Max(1, channelWidth));
        if (channel < 0 || channel >= Tracks.Count)
        {
            return;
        }

        var viewport = _session.Viewport;
        var beat = viewport.BeatAtY(position.Y);
        if (_session.SnapTime)
        {
            beat = Math.Round(beat * _session.Division, MidpointRounding.AwayFromZero) / _session.Division;
        }

        beat = Math.Max(0, beat);
        var kind = Tracks[channel];
        var current = CurrentValue(_session.CurrentLine, kind, _session.CurrentLayer, beat);
        var item = _editor.AddEvent(_session.CurrentLine, kind, _session.CurrentLayer, Beat.FromValue(beat), current);
        _session.SelectedEvents.Clear();
        _session.SelectedEvents.Add(item);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(_canvas);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        var position = point.Position;

        if (_marquee)
        {
            _marqueeCurrentX = position.X;
            _marqueeCurrentY = position.Y;
            Invalidate();
            e.Handled = true;
            return;
        }

        if (_dragEvent is null)
        {
            return;
        }

        if (!_dragMoved &&
            Math.Abs(position.X - _pressX) < DragThreshold &&
            Math.Abs(position.Y - _pressY) < DragThreshold)
        {
            return;
        }

        var viewport = _session.Viewport;
        var beat = viewport.BeatAtY(position.Y);
        if (_session.SnapTime)
        {
            beat = Math.Round(beat * _session.Division, MidpointRounding.AwayFromZero) / _session.Division;
        }

        beat = Math.Max(0, beat);

        var origin = _dragOrigins.Count > 0 ? _dragOrigins[0] : default;
        var delta = beat - origin.Start;
        foreach (var state in _dragOrigins)
        {
            state.Event.StartBeat = Beat.FromValue(state.Start + delta);
            state.Event.EndBeat = Beat.FromValue(state.End + delta);
        }

        _dragMoved = true;
        Invalidate();
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndDrag();
        e.Handled = true;
    }

    private void EndDrag()
    {
        if (_marquee)
        {
            ApplyMarqueeSelection();
            _marquee = false;
            Invalidate();
            return;
        }

        var item = _dragEvent;
        _dragEvent = null;
        if (item is null || !_dragMoved || _session is null || _dragOrigins.Count == 0)
        {
            return;
        }

        var finals = _dragOrigins
            .Select(state => new DragOrigin(state.Event, state.Kind, state.Event.StartBeat.Value, state.Event.EndBeat.Value, state.StartValue, state.EndValue))
            .ToList();
        var origins = _dragOrigins.ToList();
        ApplyStates(origins);

        _editor?.Undo.Push(new DelegateAction(
            $"移动 {origins.Count} 个事件",
            () => ApplyStates(finals),
            () => ApplyStates(origins)));

        _session.Document.IsDirty = true;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    /// <summary>按框选区域确定选中的事件。</summary>
    private void ApplyMarqueeSelection()
    {
        if (_session?.CurrentLine is null)
        {
            return;
        }

        var viewport = _session.Viewport;
        var left = Math.Min(_marqueeStartX, _marqueeCurrentX);
        var right = Math.Max(_marqueeStartX, _marqueeCurrentX);
        var top = Math.Min(_marqueeStartY, _marqueeCurrentY);
        var bottom = Math.Max(_marqueeStartY, _marqueeCurrentY);

        if (!_marqueeAppend)
        {
            _session.SelectedEvents.Clear();
        }

        for (var channel = 0; channel < Tracks.Count; channel++)
        {
            foreach (var item in _session.CurrentLine.Track(Tracks[channel], _session.CurrentLayer))
            {
                var barX = channel * (_canvas.ActualWidth / Math.Max(1, Tracks.Count)) +
                           (_canvas.ActualWidth / Math.Max(1, Tracks.Count) - Math.Max(6, _canvas.ActualWidth / Math.Max(1, Tracks.Count) * 0.62)) / 2;
                var barWidth = Math.Max(6, _canvas.ActualWidth / Math.Max(1, Tracks.Count) * 0.62);
                if (barX + barWidth < left || barX > right)
                {
                    continue;
                }

                var eventTop = viewport.YAtBeat(item.EndBeat.Value);
                var eventBottom = viewport.YAtBeat(item.StartBeat.Value);
                if (eventBottom >= top && eventTop <= bottom)
                {
                    _session.SelectedEvents.Add(item);
                }
            }
        }

        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private LineEvent? HitTestEvent(double x, double y)
    {
        if (_session?.CurrentLine is null)
        {
            return null;
        }

        var viewport = _session.Viewport;
        var channelWidth = _canvas.ActualWidth / Math.Max(1, Tracks.Count);
        var barWidth = Math.Max(6, channelWidth * 0.62);

        for (var channel = 0; channel < Tracks.Count; channel++)
        {
            var barX = channel * channelWidth + (channelWidth - barWidth) / 2;
            if (x < barX - 4 || x > barX + barWidth + 4)
            {
                continue;
            }

            foreach (var item in _session.CurrentLine.Track(Tracks[channel], _session.CurrentLayer))
            {
                var top = viewport.YAtBeat(item.EndBeat.Value);
                var bottom = viewport.YAtBeat(item.StartBeat.Value);
                if (y >= top - 3 && y <= bottom + 3)
                {
                    return item;
                }
            }
        }

        return null;
    }

}

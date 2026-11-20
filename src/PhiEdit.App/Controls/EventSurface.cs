using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PhiEdit.Core.Beats;
using PhiEdit.Core.Easing;
using PhiEdit.Core.Charts;
using PhiEdit.Core.Editing;
using PhiEdit.Core.Layout;
using PhiEdit.Core.Rendering;
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
        var gridLeft = Palette.GridInsetX;
        var gridWidth = Math.Max(1, width - Palette.GridInsetX * 2);
        var channelWidth = gridWidth / Math.Max(1, Tracks.Count);
        var barWidth = Math.Max(6, channelWidth * 0.62);

        DrawTimeGrid(drawing, session, viewport, width, playheadY);
        DrawChannelSeparators(drawing, gridLeft, playheadY, channelWidth);
        DrawEvents(drawing, session, viewport, channelWidth, barWidth, gridLeft, width, height);
        DrawHeader(drawing, gridLeft, channelWidth, gridWidth);
        DrawFooter(drawing, session, gridLeft, channelWidth, gridWidth, width, height);
        DrawMarquee(drawing);
        DrawPlayhead(drawing, width, playheadY);
    }

    private void DrawTimeGrid(CanvasDrawingSession drawing, EditorSession session, EditViewport viewport,
        double width, double playheadY)
    {
        var first = viewport.BottomBeat - 1;
        var last = viewport.TopBeat + 1;
        var left = (float)Palette.GridInsetX;
        var right = (float)Math.Max(left + 1, width - Palette.GridInsetX);
        var gridTop = Palette.GridInsetTop;
        var gridBottom = Math.Max(gridTop, playheadY - Palette.GridInsetBottom);

        foreach (var tick in GridLayout.Ticks(first, last, session.Division))
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
                    drawing.DrawLine(left, (float)y, right, (float)y, Palette.BeatLine.ToWinColor(), Palette.BeatLineWidth);
                    break;
                case GridTickKind.Subdivision:
                    drawing.DrawLine(left, (float)y, right, (float)y,
                        EditorPalette.WithAlpha(Palette.Subdivision, Palette.SubdivisionAlpha).ToWinColor(),
                        Palette.SubdivisionWidth);
                    break;
                default:
                    drawing.DrawLine(left, (float)y, right, (float)y,
                        EditorPalette.WithAlpha(Palette.MinorSubdivision, Palette.MinorSubdivisionAlpha).ToWinColor(),
                        Palette.MinorSubdivisionWidth);
                    break;
            }
        }
    }

    private void DrawChannelSeparators(CanvasDrawingSession drawing, double gridLeft, double playheadY, double channelWidth)
    {
        var color = EditorPalette.WithAlpha(Palette.EventColumnSeparator, 95).ToWinColor();
        var gridTop = (float)Palette.GridInsetTop;
        var gridBottom = (float)Math.Max(gridTop, playheadY - Palette.GridInsetBottom);
        for (var index = 0; index <= Tracks.Count; index++)
        {
            var x = (float)(gridLeft + index * channelWidth);
            drawing.DrawLine(x, gridTop, x, gridBottom, color, 1f);
        }
    }

    private void DrawEvents(CanvasDrawingSession drawing, EditorSession session, EditViewport viewport,
        double channelWidth, double barWidth, double gridLeft, double width, double height)
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

            var barX = (float)(gridLeft + channel * channelWidth + (channelWidth - barWidth) / 2);
            // 同一轨道共用数值范围，短事件与连续事件都能画出正确的缓动形状。
            var visible = events.Where(item =>
            {
                var eventTop = viewport.YAtBeat(item.EndBeat.Value);
                var eventBottom = viewport.YAtBeat(item.StartBeat.Value);
                return eventBottom >= -10 && eventTop <= height + 10;
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
                if (bottom < -10 || top > height + 10)
                {
                    continue;
                }

                var rectTop = (float)Math.Max(0, top);
                var rectBottom = (float)Math.Min(height, bottom);
                // 事件条严格按起止拍绘制：起点 4:1/2、终点 5:1/2 就是一段覆盖该区间的长条。
                var rectHeight = Math.Max(2f, rectBottom - rectTop);
                var selected = session.SelectedEvents.Contains(item);

                var fill = selected ? Palette.EventFillSelected : Palette.EventFill;
                var stroke = selected ? Palette.EventStrokeSelected : Palette.EventStroke;
                drawing.FillRectangle(new Rect(barX, rectTop, (float)barWidth, rectHeight),
                    EditorPalette.WithAlpha(fill, 64).ToWinColor());
                drawing.DrawRectangle(new Rect(barX, rectTop, (float)barWidth, rectHeight), stroke.ToWinColor(),
                    selected ? 2f : 1f);

                DrawEasingCurve(drawing, item, barX, (float)barWidth, rectTop, rectBottom, rangeMin, rangeMax);
            }
        }
    }

    /// <summary>
    /// 在事件条内绘制缓动曲线：横轴为数值（左小右大），纵轴为时间（下起点、上终点）。
    /// 这样正弦、二次等缓动会呈现原版的橙色曲线，而不是一条竖线。
    /// </summary>
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
            var eased = Easing.Evaluate(progress, item.Easing, item.EasingLeft, item.EasingRight);
            var value = item.StartValue + (item.EndValue - item.StartValue) * eased;
            var normalized = Math.Clamp((value - rangeMin) / (rangeMax - rangeMin), 0, 1);
            var point = new Vector2(
                barX + padding + (float)normalized * usableWidth,
                bottom - (float)progress * (bottom - top));
            if (previous is Vector2 last)
            {
                drawing.DrawLine(last, point, color, 1.5f);
            }

            previous = point;
        }
    }

    private void DrawHeader(CanvasDrawingSession drawing, double gridLeft, double channelWidth, double gridWidth)
    {
        drawing.FillRectangle(new Rect((float)gridLeft, 0, (float)gridWidth, (float)HeaderHeight),
            Palette.EventHeaderBackground.ToWinColor());
        var format = CanvasExtensions.TextFormat(12f);
        for (var channel = 0; channel < Tracks.Count; channel++)
        {
            var x = (float)(gridLeft + channel * channelWidth + 6);
            drawing.DrawText(Tracks[channel].DisplayName(), new Vector2(x, 4), Palette.EventHeaderText.ToWinColor(), format);
        }
    }

    /// <summary>底栏：显示各轨道在判定线处的当前数值。</summary>
    private void DrawFooter(CanvasDrawingSession drawing, EditorSession session,
        double gridLeft, double channelWidth, double gridWidth, double width, double height)
    {
        var top = (float)(height - FooterHeight);
        drawing.FillRectangle(new Rect((float)gridLeft, top, (float)gridWidth, (float)FooterHeight),
            Palette.EventHeaderBackground.ToWinColor());

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
                new Vector2((float)(gridLeft + channel * channelWidth + channelWidth / 2), top + 5),
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
        var left = (float)Palette.GridInsetX;
        var right = (float)Math.Max(left + 1, width - Palette.GridInsetX);
        drawing.DrawLine(left, (float)playheadY, right, (float)playheadY, color, Palette.JudgementWidth);
    }

    /// <summary>取某轨道在指定拍的当前数值（取该拍之前最后一条事件的结束值）。</summary>
    public static double CurrentValue(JudgeLine line, EventTrackKind kind, int layer, double beat)
    {
        var events = line.Track(kind, layer);
        double? value = null;
        foreach (var item in events)
        {
            if (beat >= item.StartBeat.Value && beat <= item.EndBeat.Value && item.DurationBeats > 1e-9)
            {
                var progress = (beat - item.StartBeat.Value) / item.DurationBeats;
                var eased = Easing.Evaluate(progress, item.Easing, item.EasingLeft, item.EasingRight);
                return item.StartValue + (item.EndValue - item.StartValue) * eased;
            }

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
                // Ctrl+单击：加选 / 取消加选；先取消音符选择，两类对象互斥。
                _session.SelectedNotes.Clear();
                if (!_session.SelectedEvents.Remove(hit))
                {
                    _session.SelectedEvents.Add(hit);
                }

                SelectionChanged?.Invoke(this, EventArgs.Empty);
                Invalidate();
                e.Handled = true;
                return;
            }

            // 无论该事件此前是否已被选中，点击事件都要先取消音符选择，
            // 否则会残留「音符与事件同时被选中」的状态。
            if (_session.SelectedEvents.Contains(hit))
            {
                _session.SelectedNotes.Clear();
            }
            else
            {
                _session.SelectEvent(hit);
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
                _session.SelectedNotes.Clear();
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
        var gridLeft = Palette.GridInsetX;
        var gridWidth = Math.Max(1, _canvas.ActualWidth - gridLeft * 2);
        var channelWidth = gridWidth / Math.Max(1, Tracks.Count);
        var channel = (int)((position.X - gridLeft) / Math.Max(1, channelWidth));
        if (position.X < gridLeft || channel < 0 || channel >= Tracks.Count)
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

        var gridLeft = Palette.GridInsetX;
        var gridWidth = Math.Max(1, _canvas.ActualWidth - gridLeft * 2);
        var channelWidth = gridWidth / Math.Max(1, Tracks.Count);
        var barWidth = Math.Max(6, channelWidth * 0.62);
        for (var channel = 0; channel < Tracks.Count; channel++)
        {
            foreach (var item in _session.CurrentLine.Track(Tracks[channel], _session.CurrentLayer))
            {
                var barX = gridLeft + channel * channelWidth + (channelWidth - barWidth) / 2;
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
        var gridLeft = Palette.GridInsetX;
        var gridWidth = Math.Max(1, _canvas.ActualWidth - gridLeft * 2);
        var channelWidth = gridWidth / Math.Max(1, Tracks.Count);
        var barWidth = Math.Max(6, channelWidth * 0.62);

        for (var channel = 0; channel < Tracks.Count; channel++)
        {
            var barX = gridLeft + channel * channelWidth + (channelWidth - barWidth) / 2;
            if (x < barX - 4 || x > barX + barWidth + 4)
            {
                continue;
            }

            foreach (var item in _session.CurrentLine.Track(Tracks[channel], _session.CurrentLayer))
            {
                var top = viewport.YAtBeat(item.EndBeat.Value);
                var bottom = viewport.YAtBeat(item.StartBeat.Value);
                var center = (top + bottom) / 2;
                if (y >= Math.Min(top, bottom) - 4 && y <= Math.Max(top, bottom) + 4)
                {
                    return item;
                }
            }
        }

        return null;
    }

}

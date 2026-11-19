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
/// 纵轴为时间，整拍线红色加粗并标注拍号；横向按事件类型分栏。
/// </summary>
public sealed class EventSurface : UserControl
{
    private const double HeaderHeight = 24;
    private const double FooterHeight = 26;

    private readonly CanvasControl _canvas = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
    };

    private EditorSession? _session;
    private ChartEditor? _editor;
    private double _playBeat;

    private LineEvent? _dragEvent;
    private Beat _dragOriginStart;
    private Beat _dragOriginEnd;
    private bool _dragMoved;

    public EventSurface()
    {
        Content = _canvas;
        _canvas.Draw += OnDraw;
        _canvas.SizeChanged += (_, _) => _canvas.Invalidate();
        _canvas.PointerPressed += OnPointerPressed;
        _canvas.PointerMoved += OnPointerMoved;
        _canvas.PointerReleased += OnPointerReleased;
        _canvas.PointerCaptureLost += (_, _) => EndDrag();
        _canvas.PointerWheelChanged += OnPointerWheelChanged;
        _canvas.DoubleTapped += OnDoubleTapped;
        _canvas.IsTabStop = true;
    }

    public EditorPalette Palette { get; set; } = new();

    public EditorSession? Session
    {
        get => _session;
        set
        {
            _session = value;
            _canvas.Invalidate();
        }
    }

    public ChartEditor? Editor
    {
        get => _editor;
        set => _editor = value;
    }

    public double PlayBeat
    {
        get => _playBeat;
        set
        {
            if (Math.Abs(_playBeat - value) < 1e-9)
            {
                return;
            }

            _playBeat = value;
            _canvas.Invalidate();
        }
    }

    public event EventHandler? SelectionChanged;

    public event EventHandler? ViewChanged;

    /// <summary>请求重绘。</summary>
    public void Invalidate() => _canvas.Invalidate();

    /// <summary>当前显示的事件轨道集合。</summary>
    public IReadOnlyList<EventTrackKind> Tracks { get; set; } = EventTrackKinds.Standard;

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

        var channelWidth = width / Math.Max(1, Tracks.Count);
        var barWidth = Math.Max(6, channelWidth * 0.62);

        DrawTimeGrid(drawing, session, viewport, width, height);
        DrawChannelSeparators(drawing, width, height, channelWidth);
        DrawEvents(drawing, session, viewport, channelWidth, barWidth, width, height);
        DrawHeader(drawing, channelWidth, width);
        DrawFooter(drawing, session, channelWidth, width, height);
    }

    private void DrawTimeGrid(CanvasDrawingSession drawing, EditorSession session, EditViewport viewport, double width, double height)
    {
        var first = viewport.TopBeat - 1;
        var last = viewport.BottomBeat + 1;

        foreach (var tick in GridLayout.Ticks(first, last, session.Division))
        {
            var y = viewport.YAtBeat(tick.Beat);
            if (y < -4 || y > height + 4)
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

    private void DrawChannelSeparators(CanvasDrawingSession drawing, double width, double height, double channelWidth)
    {
        var color = EditorPalette.WithAlpha(Palette.EventColumnSeparator, 95).ToWinColor();
        for (var index = 0; index <= Tracks.Count; index++)
        {
            var x = (float)(index * channelWidth);
            drawing.DrawLine(x, 0, x, (float)height, color, 1f);
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

    /// <summary>底栏：显示各轨道在播放头处的当前数值。</summary>
    private void DrawFooter(CanvasDrawingSession drawing, EditorSession session,
        double channelWidth, double width, double height)
    {
        var top = (float)(height - FooterHeight);
        drawing.FillRectangle(new Rect(0, top, (float)width, (float)FooterHeight), Palette.EventHeaderBackground.ToWinColor());

        var line = session.CurrentLine;
        var format = CanvasExtensions.TextFormat(13f, center: true);
        for (var channel = 0; channel < Tracks.Count; channel++)
        {
            var kind = Tracks[channel];
            var value = line is null ? 0 : CurrentValue(line, kind, session.CurrentLayer, _playBeat);
            var text = kind.FormatValue(value);
            var color = value < 0 ? Palette.EventValueBottom : Palette.EventValueTop;
            drawing.DrawText(text,
                new Vector2((float)(channel * channelWidth + channelWidth / 2), top + 5),
                color.ToWinColor(), format);
        }
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
        var hit = HitTestEvent(position.X, position.Y);

        if (hit is not null)
        {
            _session.SelectedEvents.Clear();
            _session.SelectedEvents.Add(hit);
            _dragEvent = hit;
            _dragOriginStart = hit.StartBeat;
            _dragOriginEnd = hit.EndBeat;
            _dragMoved = false;
            _canvas.CapturePointer(e.Pointer);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            _canvas.Invalidate();
            e.Handled = true;
        }
        else
        {
            _session.SelectedEvents.Clear();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            _canvas.Invalidate();
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
        _canvas.Invalidate();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragEvent is null || _session is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(_canvas);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        var position = point.Position;
        var viewport = _session.Viewport;
        var beat = viewport.BeatAtY(position.Y);
        if (_session.SnapTime)
        {
            beat = Math.Round(beat * _session.Division, MidpointRounding.AwayFromZero) / _session.Division;
        }

        beat = Math.Max(0, beat);
        var duration = _dragOriginEnd.Value - _dragOriginStart.Value;
        _dragEvent.StartBeat = Beat.FromValue(beat);
        _dragEvent.EndBeat = Beat.FromValue(beat + duration);
        _dragMoved = true;
        _canvas.Invalidate();
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndDrag();
        e.Handled = true;
    }

    private void EndDrag()
    {
        var item = _dragEvent;
        _dragEvent = null;
        if (item is null || !_dragMoved || _session is null)
        {
            return;
        }

        var originStart = _dragOriginStart;
        var originEnd = _dragOriginEnd;
        var finalStart = item.StartBeat;
        var finalEnd = item.EndBeat;

        item.StartBeat = originStart;
        item.EndBeat = originEnd;

        _editor?.Undo.Push(new DelegateAction(
            "移动事件",
            () =>
            {
                item.StartBeat = finalStart;
                item.EndBeat = finalEnd;
            },
            () =>
            {
                item.StartBeat = originStart;
                item.EndBeat = originEnd;
            }));

        _session.Document.IsDirty = true;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        _canvas.Invalidate();
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

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        var delta = e.GetCurrentPoint(_canvas).Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        var position = e.GetCurrentPoint(_canvas).Position;
        var viewport = _session.Viewport;
        if (e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control))
        {
            viewport.ZoomAt(position.Y, delta > 0 ? 1.15 : 1 / 1.15);
        }
        else
        {
            viewport.ScrollByPixels(-delta * 0.6);
        }

        ViewChanged?.Invoke(this, EventArgs.Empty);
        _canvas.Invalidate();
        e.Handled = true;
    }
}

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
using System.Numerics;
using Windows.Foundation;

namespace PhiEdit.App.Controls;

/// <summary>
/// 音符区：编辑当前判定线上的音符。
///
/// 横轴为舞台 X 坐标（竖线网格用于对齐）；纵轴为时间，自下而上流逝——
/// 判定线（播放头）固定在画布底部，网格线只绘制在判定线以上，音符则绘制到离开可视区为止。
/// </summary>
public sealed class NoteSurface : UserControl
{
    /// <summary>触发拖动所需的最小位移（像素），避免点击选中时误触发拖动。</summary>
    private const double DragThreshold = 3;

    private readonly CanvasControl _canvas = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
    };

    private EditorSession? _session;
    private ChartEditor? _editor;

    private Note? _dragNote;
    private double _pressX;
    private double _pressY;
    private bool _dragMoved;

    /// <summary>拖动开始时整组选中项的原始状态，用于生成单次撤销记录。</summary>
    private readonly List<DragOrigin> _dragOrigins = new();

    private readonly record struct DragOrigin(Note Note, double Beat, double End, double X);

    private bool _marquee;
    private double _marqueeStartX;
    private double _marqueeStartY;
    private double _marqueeCurrentX;
    private double _marqueeCurrentY;
    private bool _marqueeAppend;

    public NoteSurface()
    {
        Content = _canvas;
        _canvas.Draw += OnDraw;
        _canvas.SizeChanged += (_, _) => Invalidate();
        _canvas.PointerPressed += OnPointerPressed;
        _canvas.PointerMoved += OnPointerMoved;
        _canvas.PointerReleased += OnPointerReleased;
        _canvas.PointerCanceled += OnPointerReleased;
        _canvas.PointerCaptureLost += (_, _) => EndDrag();
        _canvas.IsTabStop = true;
    }

    /// <summary>编辑器配色。</summary>
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

    /// <summary>交互导致数据变化时触发，供外部刷新属性面板。</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>请求重绘。</summary>
    public void Invalidate() => _canvas.Invalidate();

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var session = _session;
        var drawing = args.DrawingSession;
        drawing.Clear(Palette.Surface.ToWinColor());

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
        viewport.NoteWidth = width;

        var playheadY = viewport.PlayheadY;

        DrawVerticalGrid(drawing, session, viewport, width, playheadY);
        DrawTimeGrid(drawing, session, viewport, width, playheadY);
        DrawNotes(drawing, session, viewport, width, height);
        DrawMarquee(drawing);
        DrawPlayhead(drawing, viewport, width, playheadY);
    }

    /// <summary>竖线网格：黄色细线；竖线数量为奇数时正中竖线加粗。只绘制在判定线以上。</summary>
    private void DrawVerticalGrid(CanvasDrawingSession drawing, EditorSession session, EditViewport viewport,
        double width, double playheadY)
    {
        var grid = session.Grid;
        foreach (var stageX in grid.Lines())
        {
            var x = viewport.StageXToPixel(stageX);
            if (x < -2 || x > width + 2)
            {
                continue;
            }

            var isCenter = Math.Abs(stageX) < 1e-6;
            var strokeWidth = isCenter && grid.HasCenterLine ? Palette.VerticalCenterWidth : Palette.VerticalWidth;
            drawing.DrawLine((float)x, 0, (float)x, (float)playheadY,
                EditorPalette.WithAlpha(Palette.VerticalLine, Palette.VerticalAlpha).ToWinColor(), strokeWidth);
        }
    }

    /// <summary>时间网格：整拍线为红色加粗并标注拍号，相邻整拍线之间为黄色细线。</summary>
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
                    DrawBeatLabel(drawing, tick.Beat, width, y);
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

    private void DrawBeatLabel(CanvasDrawingSession drawing, double beat, double width, double y)
    {
        var text = beat.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        var format = CanvasExtensions.TextFormat(12f, right: true);
        drawing.DrawText(text, new Vector2((float)(width - 8), (float)(y - 16)), Palette.BeatLabel.ToWinColor(), format);
    }

    /// <summary>判定线（播放头）：固定在画布底部的粗灰线，压在所有内容之上。</summary>
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

    private void DrawPlayhead(CanvasDrawingSession drawing, EditViewport viewport, double width, double playheadY)
    {
        var color = EditorPalette.WithAlpha(Palette.Judgement, Palette.JudgementAlpha).ToWinColor();
        drawing.DrawLine(0, (float)playheadY, (float)width, (float)playheadY, color, Palette.JudgementWidth);
    }

    private void DrawNotes(CanvasDrawingSession drawing, EditorSession session, EditViewport viewport, double width, double height)
    {
        var line = session.CurrentLine;
        if (line is null)
        {
            return;
        }

        var noteWidth = NoteWidth(width);
        const float noteHeight = 9f;

        foreach (var note in line.Notes)
        {
            var y = viewport.YAtBeat(note.StartBeat.Value);
            var endY = viewport.YAtBeat(note.EndBeat.Value);
            if (Math.Max(y, endY) < -30 || Math.Min(y, endY) > height + 30)
            {
                continue;
            }

            var x = (float)viewport.StageXToPixel(note.PositionX);
            var color = Palette.NoteColor(note.Type).ToWinColor();
            var selected = session.SelectedNotes.Contains(note);

            if (note.Type == NoteType.Hold && Math.Abs(endY - y) > 0.5)
            {
                var top = (float)Math.Min(y, endY);
                var bodyHeight = (float)Math.Abs(endY - y);
                drawing.FillRoundedRectangle(new Rect(x - noteWidth / 2, top, noteWidth, bodyHeight), 4, 4,
                    EditorPalette.WithAlpha(Palette.NoteHold, 110).ToWinColor());
                drawing.DrawRoundedRectangle(new Rect(x - noteWidth / 2, top, noteWidth, bodyHeight), 4, 4, color, 1.5f);
            }

            var headY = (float)y - noteHeight / 2;
            drawing.FillRoundedRectangle(new Rect(x - noteWidth / 2, headY, noteWidth, noteHeight), 4, 4, color);
            if (selected)
            {
                drawing.DrawRoundedRectangle(new Rect(x - noteWidth / 2 - 3, headY - 3, noteWidth + 6, noteHeight + 6), 6, 6,
                    Palette.Selection.ToWinColor(), 2f);
            }
        }
    }

    private static double NoteWidth(double paneWidth) => Math.Max(28, Math.Min(96, paneWidth * 0.12));

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(_canvas);
        if (point.Properties.IsRightButtonPressed)
        {
            return;
        }

        var position = point.Position;
        _pressX = position.X;
        _pressY = position.Y;

        var ctrl = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift);
        var hit = HitTestNote(position.X, position.Y);

        if (hit is not null)
        {
            if (ctrl)
            {
                // Ctrl+单击：加选 / 取消加选，不进入拖动。
                if (!_session.SelectedNotes.Remove(hit))
                {
                    _session.SelectedNotes.Add(hit);
                }

                SelectionChanged?.Invoke(this, EventArgs.Empty);
                Invalidate();
                e.Handled = true;
                return;
            }

            if (!_session.SelectedNotes.Contains(hit))
            {
                _session.SelectedNotes.Clear();
                _session.SelectedNotes.Add(hit);
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }

            _dragNote = hit;
            _dragMoved = false;
            CaptureDragOrigins();
            _canvas.CapturePointer(e.Pointer);
        }
        else if (_session.Tool is not null && !shift && !ctrl)
        {
            // 放置音符：先清空选择。
            _session.SelectedNotes.Clear();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            PlaceNote(position.X, position.Y);
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
                _session.SelectedNotes.Clear();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }

            _canvas.CapturePointer(e.Pointer);
        }

        Invalidate();
        e.Handled = true;
    }

    /// <summary>记录整组选中项的原始状态。</summary>
    private void CaptureDragOrigins()
    {
        _dragOrigins.Clear();
        foreach (var note in _session.SelectedNotes)
        {
            _dragOrigins.Add(new DragOrigin(note, note.StartBeat.Value, note.EndBeat.Value, note.PositionX));
        }
    }

    /// <summary>把一组状态写回音符。</summary>
    private static void ApplyStates(IEnumerable<DragOrigin> states)
    {
        foreach (var state in states)
        {
            state.Note.StartBeat = Beat.FromValue(state.Beat);
            state.Note.EndBeat = Beat.FromValue(state.End);
            state.Note.PositionX = state.X;
        }
    }

    private void PlaceNote(double x, double y)
    {
        if (_session?.CurrentLine is null || _editor is null)
        {
            return;
        }

        var viewport = _session.Viewport;
        var beat = viewport.BeatAtY(y);
        if (_session.SnapTime)
        {
            beat = Math.Round(beat * _session.Division, MidpointRounding.AwayFromZero) / _session.Division;
        }

        beat = Math.Max(0, beat);

        var stageX = viewport.PixelToStageX(x);
        if (_session.SnapX)
        {
            stageX = _session.Grid.Snap(stageX);
        }

        var note = _editor.PlaceNote(_session.CurrentLine, _session.Tool!.Value, Beat.FromValue(beat), stageX);
        _session.SelectedNotes.Clear();
        _session.SelectedNotes.Add(note);
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

        if (_dragNote is null)
        {
            return;
        }

        // 位移小于阈值时仍算「点击选中」，不触发拖动。
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

        var stageX = viewport.PixelToStageX(position.X);
        if (_session.SnapX)
        {
            stageX = _session.Grid.Snap(stageX);
        }

        _dragMoved = true;
        MoveDragged(beat, stageX);
        Invalidate();
        e.Handled = true;
    }

    /// <summary>按目标拍与横坐标移动整组选中项。</summary>
    private void MoveDragged(double beat, double stageX)
    {
        if (_dragOrigins.Count == 0 || _dragNote is null)
        {
            return;
        }

        var origin = _dragOrigins[0];
        var deltaBeat = beat - origin.Beat;
        var deltaX = stageX - origin.X;

        foreach (var state in _dragOrigins)
        {
            state.Note.StartBeat = Beat.FromValue(state.Beat + deltaBeat);
            state.Note.EndBeat = Beat.FromValue(state.End + deltaBeat);
            state.Note.PositionX = state.X + deltaX;
        }
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

        var note = _dragNote;
        _dragNote = null;
        if (note is null || !_dragMoved || _session is null || _dragOrigins.Count == 0)
        {
            return;
        }

        // 记录拖动后的状态，再还原为起点，由撤销栈形成单次撤销记录。
        var finals = _dragOrigins
            .Select(state => new DragOrigin(state.Note, state.Note.StartBeat.Value, state.Note.EndBeat.Value, state.Note.PositionX))
            .ToList();
        var origins = _dragOrigins.ToList();
        ApplyStates(origins);

        _editor?.Undo.Push(new DelegateAction(
            $"移动 {origins.Count} 个音符",
            () => ApplyStates(finals),
            () => ApplyStates(origins)));

        _session.Document.IsDirty = true;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    /// <summary>按框选区域确定选中的音符。</summary>
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

        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private Note? HitTestNote(double x, double y)
    {
        if (_session?.CurrentLine is null)
        {
            return null;
        }

        var viewport = _session.Viewport;
        var noteWidth = NoteWidth(_canvas.ActualWidth);

        foreach (var note in _session.CurrentLine.Notes)
        {
            var noteY = viewport.YAtBeat(note.StartBeat.Value);
            var noteX = viewport.StageXToPixel(note.PositionX);
            if (Math.Abs(noteY - y) <= 9 && Math.Abs(noteX - x) <= noteWidth / 2 + 2)
            {
                return note;
            }
        }

        return null;
    }

}

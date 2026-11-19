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
/// 横轴为舞台 X 坐标（竖线网格用于对齐），纵轴为时间（整拍线带拍号，细线表示几分之几拍）。
/// </summary>
public sealed class NoteSurface : UserControl
{
    private readonly CanvasControl _canvas = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
    };

    private EditorSession? _session;
    private ChartEditor? _editor;
    private double _playBeat;

    private Note? _dragNote;
    private Beat _dragOriginBeat;
    private double _dragOriginX;
    private double _dragOriginY;
    private bool _dragMoved;

    public NoteSurface()
    {
        Content = _canvas;
        _canvas.Draw += OnDraw;
        _canvas.SizeChanged += (_, _) => _canvas.Invalidate();
        _canvas.PointerPressed += OnPointerPressed;
        _canvas.PointerMoved += OnPointerMoved;
        _canvas.PointerReleased += OnPointerReleased;
        _canvas.PointerCanceled += OnPointerReleased;
        _canvas.PointerCaptureLost += (_, _) => EndDrag();
        _canvas.PointerWheelChanged += OnPointerWheelChanged;
        _canvas.IsTabStop = true;
    }

    /// <summary>编辑器配色。</summary>
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

    /// <summary>播放头位置（拍），判定线指示与跟随滚动依赖它。</summary>
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

    /// <summary>交互导致数据变化时触发，供外部刷新属性面板。</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>视图滚动或缩放变化时触发。</summary>
    public event EventHandler? ViewChanged;

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

        DrawVerticalGrid(drawing, session, viewport, width, height);
        DrawTimeGrid(drawing, session, viewport, width, height);
        DrawJudgement(drawing, viewport, width, height);
        DrawNotes(drawing, session, viewport, width, height);
    }

    /// <summary>竖线网格：黄色细线；竖线数量为奇数时正中竖线加粗。</summary>
    private void DrawVerticalGrid(CanvasDrawingSession drawing, EditorSession session, EditViewport viewport, double width, double height)
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
            drawing.DrawLine((float)x, 0, (float)x, (float)height,
                EditorPalette.WithAlpha(Palette.VerticalLine, Palette.VerticalAlpha).ToWinColor(), strokeWidth);
        }
    }

    /// <summary>时间网格：整拍线为红色加粗并标注拍号，相邻整拍线之间为黄色细线。</summary>
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

    /// <summary>判定线指示：跟随播放头的白色横线。</summary>
    private void DrawJudgement(CanvasDrawingSession drawing, EditViewport viewport, double width, double height)
    {
        var y = viewport.YAtBeat(_playBeat);
        if (y < -8 || y > height + 8)
        {
            return;
        }

        var color = EditorPalette.WithAlpha(Palette.Judgement, Palette.JudgementAlpha).ToWinColor();
        drawing.DrawLine(0, (float)y, (float)width, (float)y, color, Palette.JudgementWidth);
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
        var hit = HitTestNote(position.X, position.Y);
        if (hit is not null)
        {
            if (!_session.SelectedNotes.Contains(hit))
            {
                _session.SelectedNotes.Clear();
                _session.SelectedNotes.Add(hit);
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }

            _dragNote = hit;
            _dragOriginBeat = hit.StartBeat;
            _dragOriginX = hit.PositionX;
            _dragOriginY = hit.PositionY;
            _dragMoved = false;
            _canvas.CapturePointer(e.Pointer);
        }
        else
        {
            _session.SelectedNotes.Clear();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            PlaceNote(position.X, position.Y);
        }

        _canvas.Invalidate();
        e.Handled = true;
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

        var note = _editor.PlaceNote(_session.CurrentLine, _session.Tool, Beat.FromValue(beat), stageX);
        _session.SelectedNotes.Clear();
        _session.SelectedNotes.Add(note);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        _canvas.Invalidate();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragNote is null || _session is null)
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

        var stageX = viewport.PixelToStageX(position.X);
        if (_session.SnapX)
        {
            stageX = _session.Grid.Snap(stageX);
        }

        _dragMoved = true;
        _dragNote.StartBeat = Beat.FromValue(beat);
        _dragNote.PositionX = stageX;
        _dragNote.PositionY = _dragOriginY;
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
        var note = _dragNote;
        _dragNote = null;
        if (note is null || !_dragMoved || _session is null)
        {
            return;
        }

        var editor = _editor;
        var originBeat = _dragOriginBeat;
        var originX = _dragOriginX;
        var originY = _dragOriginY;
        var finalBeat = note.StartBeat;
        var finalX = note.PositionX;
        var finalY = note.PositionY;
        var finalEnd = note.EndBeat;

        // 先回到起点，再由撤销栈重新应用，形成单次撤销记录。
        note.StartBeat = originBeat;
        note.EndBeat = Beat.FromValue(originBeat.Value);
        note.PositionX = originX;
        note.PositionY = originY;

        editor?.Undo.Push(new DelegateAction(
            "移动音符",
            () =>
            {
                note.StartBeat = finalBeat;
                note.EndBeat = finalEnd;
                note.PositionX = finalX;
                note.PositionY = finalY;
            },
            () =>
            {
                note.StartBeat = originBeat;
                note.EndBeat = Beat.FromValue(originBeat.Value);
                note.PositionX = originX;
                note.PositionY = originY;
            }));

        _session.Document.IsDirty = true;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        _canvas.Invalidate();
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

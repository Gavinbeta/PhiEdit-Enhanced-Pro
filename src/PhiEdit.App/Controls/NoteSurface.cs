using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PhiEdit.Core.Beats;
using PhiEdit.Core.Skins;
using PhiEdit.Core.Charts;
using PhiEdit.Core.Editing;
using PhiEdit.Core.Layout;
using PhiEdit.Core.Rendering;
using PhiEdit.App.Services;
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
        _canvas.CreateResources += (sender, _) => EnsureSkinLoaded(sender);
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

    /// <summary>资源包贴图渲染器；为空时退回纯色绘制。</summary>
    public NoteSkinRenderer? Renderer { get; set; }

    /// <summary>音符大小设置（原版 noteSize，默认 200）。</summary>
    public double NoteSize { get; set; } = 200;

    /// <summary>用于加载资源包贴图的资源创建器。</summary>
    public ICanvasResourceCreator CanvasResourceCreator => _canvas;

    /// <summary>贴图加载失败时对外报告。</summary>
    public event EventHandler<string>? SkinLoadFailed;

    /// <summary>
    /// 请求重新加载当前生效的资源包贴图。
    /// Win2D 的画布设备在首次绘制或 CreateResources 时才创建，
    /// 因此这里通过 TryCreateResources 确保在设备就绪后再加载贴图。
    /// </summary>
    public void RequestSkinReload()
    {
        _skinReloadPending = true;
        Invalidate();
    }

    private bool _skinLoading;

    private bool _skinReloadPending;

    /// <summary>
    /// 确保贴图已加载。画布设备在首次绘制时才就绪，
    /// 因此在绘制回调里启动加载，完成后再触发一次重绘。
    /// </summary>
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

    private async Task LoadSkinAsync(ICanvasResourceCreator creator, PhiEdit.Core.Skins.NoteSkin skin,
        NoteSkinRenderer renderer)
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

        EnsureSkinLoaded(sender);

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

    /// <summary>
    /// 音符缩放：与原版一致，音符基准宽度随编辑区宽度与音符大小设置等比变化。
    /// </summary>
    private double NoteScale(double paneWidth) => NoteSize * paneWidth / 1350.0;

    /// <summary>
    /// 音符基准宽度（原版 noteBaseWidth = 68 × renderNoteScale × |size|，
    /// 其中 renderNoteScale = noteSize × 画布宽 / (1350 × 68)）。
    /// </summary>
    private static double BaseWidth(double scale, Note note) => scale * Math.Abs(note.Size);

    private void DrawNotes(CanvasDrawingSession drawing, EditorSession session, EditViewport viewport, double width, double height)
    {
        var line = session.CurrentLine;
        if (line is null)
        {
            return;
        }

        var renderer = Renderer;
        var skin = renderer?.Skin;
        var textured = renderer is not null && renderer.IsReady;
        var scale = NoteScale(width);

        // 高亮 = 多押提示（与其它音符同时刻），与「选中」无关。
        var simultaneous = session.Document.SimultaneousNotes();

        foreach (var note in line.Notes)
        {
            var y = viewport.YAtBeat(note.StartBeat.Value);
            var endY = viewport.YAtBeat(note.EndBeat.Value);
            if (Math.Max(y, endY) < -60 || Math.Min(y, endY) > height + 60)
            {
                continue;
            }

            var x = (float)viewport.StageXToPixel(note.PositionX);
            var selected = session.SelectedNotes.Contains(note);
            var highlighted = simultaneous.Contains(note);
            var alpha = note.IsFake ? 0.45f : 1f;

            if (textured && skin is not null &&
                DrawTextured(drawing, renderer, skin, note, x, y, endY, scale, highlighted, alpha))
            {
                if (selected)
                {
                    // 选中特效：绿色描边 + 半透明绿填充，范围与贴图实际区域一致。
                    var bounds = NoteBounds(renderer, skin, note, x, y, endY, scale, highlighted);
                    drawing.FillRectangle(bounds, EditorPalette.WithAlpha(Palette.SelectionFill, 90).ToWinColor());
                    drawing.DrawRectangle(bounds, Palette.Selection.ToWinColor(), 2f);
                }

                continue;
            }

            // 无贴图时的纯色绘制。
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

    /// <summary>
    /// 音符在屏幕上的实际绘制区域：Hold 覆盖头部到身体的整段，
    /// 其它音符只有头部。用于选中特效与命中测试。
    /// </summary>
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
                var atlas = highlighted ? skin.Info.HoldAtlasMH : skin.Info.HoldAtlas;
                var textureScale = noteWidth / bitmap.Size.Width;
                var headHeight = (float)(atlas[1] * textureScale);
                var tailHeight = (float)(atlas[0] * textureScale);
                var top = (float)Math.Min(y, endY);
                var bottom = (float)Math.Max(y, endY);
                return new Rect(x - noteWidth / 2, top, noteWidth, Math.Max(headHeight + tailHeight, bottom - top));
            }
        }

        var texture = renderer[highlighted ? "click_mh.png" : "click.png"];
        var height = noteWidth * (texture?.Size.Height ?? 8) / Math.Max(1, texture?.Size.Width ?? 1);
        return new Rect(x - noteWidth / 2, (float)y - height / 2, noteWidth, height);
    }

    /// <summary>使用资源包贴图绘制音符；缺少贴图时返回 false 以退回纯色绘制。</summary>
    private static bool DrawTextured(CanvasDrawingSession drawing, NoteSkinRenderer renderer, NoteSkin skin,
        Note note, float x, double y, double endY, double scale, bool highlighted, float opacity)
    {
        var baseWidth = BaseWidth(scale, note);
        var noteWidth = baseWidth * (highlighted ? skin.HighlightScale : 1);

        if (note.Type == NoteType.Hold)
        {
            return DrawHoldTextured(drawing, renderer, skin, x, y, endY, noteWidth, highlighted, opacity);
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

        // 高度按贴图宽高比推导，保持资源包的原始比例。
        var height = noteWidth * bitmap.Size.Height / Math.Max(1, bitmap.Size.Width);
        drawing.DrawImage(bitmap,
            new Rect(x - (float)noteWidth / 2, (float)y - (float)height / 2, (float)noteWidth, (float)height),
            new Rect(0, 0, bitmap.Size.Width, bitmap.Size.Height),
            opacity);
        return true;
    }

    /// <summary>
    /// 绘制 Hold 音符：按 holdAtlas 拆成头部、身体、尾部三段。
    /// 源图自上而下为「尾部 → 头部 → 身体」，与原版 note-skin.mjs 一致。
    /// </summary>
    private static bool DrawHoldTextured(CanvasDrawingSession drawing, NoteSkinRenderer renderer, NoteSkin skin,
        float x, double y, double endY, double noteWidth, bool highlighted, float opacity)
    {
        var name = highlighted ? "hold_mh.png" : "hold.png";
        var bitmap = renderer[name];
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

        // 时间自下而上流逝：起点（头部）在下方，终点（尾部）在上方。
        var headY = Math.Max(y, endY);
        var tailY = Math.Min(y, endY);
        var textureScale = noteWidth / bitmap.Size.Width;
        var headHeight = headPixels * textureScale;
        var tailHeight = tailPixels * textureScale;
        var half = (float)noteWidth / 2;

        // 身体：头部上方到尾部下方之间。
        var bodyTop = tailY + tailHeight;
        var bodyBottom = headY - headHeight;
        if (bodyBottom > bodyTop)
        {
            drawing.DrawImage(bitmap,
                new Rect(x - half, (float)bodyTop, (float)noteWidth, (float)(bodyBottom - bodyTop)),
                new Rect(0, tailPixels, bitmap.Size.Width, bodyPixels),
                opacity);
        }

        // 头部贴图：源图底部的一整段。
        drawing.DrawImage(bitmap,
            new Rect(x - half, (float)(headY - headHeight), (float)noteWidth, (float)headHeight),
            new Rect(0, imageHeight - headPixels, bitmap.Size.Width, headPixels),
            opacity);

        // 尾部贴图：源图顶部的一整段。
        if (tailHeight > 0)
        {
            drawing.DrawImage(bitmap,
                new Rect(x - half, (float)tailY, (float)noteWidth, (float)tailHeight),
                new Rect(0, 0, bitmap.Size.Width, tailPixels),
                opacity);
        }

        return true;
    }

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
                _session.SelectedEvents.Clear();
                if (!_session.SelectedNotes.Remove(hit))
                {
                    _session.SelectedNotes.Add(hit);
                }

                SelectionChanged?.Invoke(this, EventArgs.Empty);
                Invalidate();
                e.Handled = true;
                return;
            }

            // 无论该音符此前是否已被选中，点击音符都要先取消事件选择，
            // 否则会残留「音符与事件同时被选中」的状态。
            if (_session.SelectedNotes.Contains(hit))
            {
                _session.SelectedEvents.Clear();
            }
            else
            {
                _session.SelectNote(hit);
            }

            SelectionChanged?.Invoke(this, EventArgs.Empty);

            _dragNote = hit;
            _dragMoved = false;
            CaptureDragOrigins();
            _canvas.CapturePointer(e.Pointer);
        }
        else if (_session.Tool is not null && !shift && !ctrl)
        {
            // 放置音符：先清空全部选择（含事件）。
            _session.SelectedNotes.Clear();
            _session.SelectedEvents.Clear();
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
                _session.SelectedEvents.Clear();
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
        var scale = NoteScale(_canvas.ActualWidth);

        foreach (var note in _session.CurrentLine.Notes)
        {
            var noteY = viewport.YAtBeat(note.StartBeat.Value);
            var noteEndY = viewport.YAtBeat(note.EndBeat.Value);
            var noteX = viewport.StageXToPixel(note.PositionX);
            var noteWidth = BaseWidth(scale, note);
            if (Math.Abs(noteX - x) > noteWidth / 2 + 2)
            {
                continue;
            }

            if (note.Type == NoteType.Hold)
            {
                // Hold 的贴图任意位置（头部到身体）都应能命中并拖动。
                var top = Math.Min(noteY, noteEndY);
                var bottom = Math.Max(noteY, noteEndY);
                if (y >= top - 6 && y <= bottom + 6)
                {
                    return note;
                }

                continue;
            }

            if (Math.Abs(noteY - y) <= 9)
            {
                return note;
            }
        }

        return null;
    }

}

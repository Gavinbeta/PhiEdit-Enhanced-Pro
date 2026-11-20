using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhiEdit.Core.Editing;
using PhiEdit.Core.Layout;
using PhiEdit.Core.Rendering;
using System.Globalization;
using System.Numerics;

namespace PhiEdit.App.Controls;

/// <summary>
/// 左右编辑区共用的中央拍号槽：绘制分割线与整拍号。
///
/// 原版把音符区与判定线区当作同一块时间画布，只在正中留出一条窄槽放拍号；
/// WinUI 版本使用两个 CanvasControl，这里用同样的视口把分割线与拍号画在中间，
/// 两侧网格则在各自边界处留出相同宽度的空白。
/// </summary>
public sealed class BeatRuler : UserControl
{
    private readonly CanvasControl _canvas = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
    };

    private EditorSession? _session;

    public BeatRuler()
    {
        Content = _canvas;
        _canvas.Draw += OnDraw;
        _canvas.SizeChanged += (_, _) => Invalidate();
    }

    public EditorPalette Palette { get; set; } = new();

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

    private void OnViewChanged(object? sender, EventArgs e) => Invalidate();

    public void Invalidate() => _canvas.Invalidate();

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var drawing = args.DrawingSession;
        drawing.Clear(Palette.SurfaceAlt.ToWinColor());

        var session = _session;
        var width = _canvas.ActualWidth;
        var height = _canvas.ActualHeight;
        if (session is null || width <= 0 || height <= 0)
        {
            return;
        }

        var viewport = session.Viewport;
        viewport.ViewportHeight = height;
        var playheadY = viewport.PlayheadY;
        var top = (float)Palette.GridInsetTop;
        var bottom = (float)Math.Max(top, playheadY - Palette.GridInsetBottom);
        var centerX = (float)(width / 2);

        drawing.DrawLine(centerX, top, centerX, bottom,
            EditorPalette.WithAlpha(Palette.BeatLabel, 150).ToWinColor(), 1.5f);

        var format = CanvasExtensions.TextFormat(12f, center: true);
        var first = viewport.BottomBeat - 1;
        var last = viewport.TopBeat + 1;
        foreach (var tick in GridLayout.Ticks(first, last, session.Division))
        {
            if (tick.Kind != GridTickKind.Beat || tick.Beat < 0)
            {
                continue;
            }

            var y = viewport.YAtBeat(tick.Beat);
            if (y < top - 4 || y > bottom + 4)
            {
                continue;
            }

            drawing.DrawText(tick.Beat.ToString("0.##", CultureInfo.InvariantCulture),
                new Vector2(centerX, (float)(y - 16)), Palette.BeatLabel.ToWinColor(), format);
        }
    }
}

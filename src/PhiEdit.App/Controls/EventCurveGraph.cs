using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhiEdit.Core.Charts;
using System.Numerics;
using Windows.Foundation;

namespace PhiEdit.App.Controls;

/// <summary>
/// 事件详情页中的完整缓动曲线图：坐标轴箭头、橙色曲线、缓动边界滑杆。
/// 与原版属性页的曲线预览保持一致，但分辨率独立，不依赖 1350×900 舞台。
/// </summary>
public sealed class EventCurveGraph : UserControl
{
    private readonly CanvasControl _canvas = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
    };

    private LineEvent? _item;

    public EventCurveGraph()
    {
        Content = _canvas;
        _canvas.Draw += OnDraw;
        _canvas.SizeChanged += (_, _) => Invalidate();
    }

    public LineEvent? Event
    {
        get => _item;
        set
        {
            _item = value;
            Invalidate();
        }
    }

    public void Invalidate() => _canvas.Invalidate();

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var drawing = args.DrawingSession;
        var width = _canvas.ActualWidth;
        var height = _canvas.ActualHeight;
        drawing.Clear(Windows.UI.Color.FromArgb(255, 32, 35, 40));
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var left = 34f;
        var right = (float)Math.Max(left + 40, width - 34);
        var top = 16f;
        var bottom = (float)Math.Max(top + 30, height - 46);
        var axis = Windows.UI.Color.FromArgb(255, 226, 232, 240);
        var curve = Windows.UI.Color.FromArgb(255, 255, 140, 24);
        var green = Windows.UI.Color.FromArgb(255, 60, 220, 90);

        // 坐标轴与箭头
        drawing.DrawLine(left, bottom, left, top, axis, 1.5f);
        DrawArrow(drawing, new Vector2(left, top), new Vector2(left - 5, top + 9), axis);
        DrawArrow(drawing, new Vector2(left, top), new Vector2(left + 5, top + 9), axis);
        drawing.DrawLine(left, bottom, right, bottom, axis, 1.5f);
        DrawArrow(drawing, new Vector2(right, bottom), new Vector2(right - 9, bottom - 5), axis);
        DrawArrow(drawing, new Vector2(right, bottom), new Vector2(right - 9, bottom + 5), axis);

        // 数值条：左右各一条，位置对应缓动左右边界
        var item = _item;
        var easingLeft = item?.EasingLeft ?? 0;
        var easingRight = item?.EasingRight ?? 1;
        for (var row = 0; row < 2; row++)
        {
            var y = bottom + 12 + row * 15;
            drawing.DrawLine(left, y, right, y, green, 1f);
            var ratio = row == 0 ? easingLeft : easingRight;
            var handleX = left + (float)Math.Clamp(ratio, 0, 1) * (right - left);
            drawing.FillRoundedRectangle(new Rect(handleX - 7, y - 4, 14, 8), 2, 2, green);
            DrawArrow(drawing, new Vector2(handleX, y + 5), new Vector2(handleX - 4, y), green);
            DrawArrow(drawing, new Vector2(handleX, y + 5), new Vector2(handleX + 4, y), green);
        }

        if (item is null)
        {
            return;
        }

        // 右侧绿色终点标记
        drawing.DrawLine(right + 8, top, right + 8, bottom, green, 5f);
        DrawArrow(drawing, new Vector2(right + 8, bottom), new Vector2(right + 2, bottom - 8), green);
        DrawArrow(drawing, new Vector2(right + 8, bottom), new Vector2(right + 14, bottom - 8), green);

        Vector2? previous = null;
        for (var index = 0; index <= 120; index++)
        {
            var progress = index / 120.0;
            var eased = PhiEdit.Core.Easing.Easing.Evaluate(progress, item.Easing, easingLeft, easingRight);
            var point = new Vector2(left + (float)progress * (right - left),
                bottom - (float)eased * (bottom - top));
            if (previous is Vector2 last)
            {
                drawing.DrawLine(last, point, curve, 2.5f);
            }

            previous = point;
        }
    }

    private static void DrawArrow(CanvasDrawingSession drawing, Vector2 tip, Vector2 wing, Windows.UI.Color color)
    {
        drawing.DrawLine(tip, wing, color, 1.5f);
    }
}

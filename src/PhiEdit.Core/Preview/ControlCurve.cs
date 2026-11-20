using PhiEdit.Core.Charts;
using EasingMath = PhiEdit.Core.Easing.Easing;

namespace PhiEdit.Core.Preview;

/// <summary>
/// 判定线控制曲线。移植自参考源码 core/scene.mjs 的 ControlCurve。
/// </summary>
public sealed class ControlCurve
{
    private readonly IReadOnlyList<ControlPoint> _points;
    private readonly string _property;

    public ControlCurve(IEnumerable<ControlPoint> points, string property)
    {
        _property = property;
        _points = points.OrderBy(point => point.X).ToList();
    }

    public double Value(double distance)
    {
        if (_points.Count <= 1)
        {
            return _property == "skew" ? 0 : 1;
        }

        var low = 0;
        var high = _points.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (distance < _points[middle].X)
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }

        if (low == 0)
        {
            return Read(_points[0]);
        }

        if (low >= _points.Count)
        {
            return Read(_points[^1]);
        }

        var start = _points[low - 1];
        var end = _points[low];
        var span = end.X - start.X;
        var progress = Math.Abs(span) < 1e-12 ? 0 : Math.Clamp((distance - start.X) / span, 0, 1);
        var amount = EasingMath.Evaluate(progress, end.Easing);
        return Read(start) + (Read(end) - Read(start)) * amount;
    }

    private double Read(ControlPoint point) => point.Y;
}

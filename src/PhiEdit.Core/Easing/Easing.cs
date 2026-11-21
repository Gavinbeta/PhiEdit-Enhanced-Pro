namespace PhiEdit.Core.Easing;

/// <summary>
/// 标准缓动函数（与原版 RPE 一致的 32 种）与贝塞尔缓动。
/// 用于事件数值的插值与编辑区中事件条的曲线绘制。
/// </summary>
public static class Easing
{
    private const double HalfPi = Math.PI / 2;
    private const double Back = 1.70158;
    private const double BackInOut = Back * 1.525;
    private const double Elastic = 2 * Math.PI / 3;
    private const double ElasticInOut = 2 * Math.PI / 4.5;

    /// <summary>缓动种类数量。</summary>
    public const int Count = 32;

    private static double BounceOut(double progress)
    {
        if (progress < 1 / 2.75)
        {
            return 7.5625 * progress * progress;
        }

        if (progress < 2 / 2.75)
        {
            return 7.5625 * Math.Pow(progress - 1.5 / 2.75, 2) + 0.75;
        }

        if (progress < 2.5 / 2.75)
        {
            return 7.5625 * Math.Pow(progress - 2.25 / 2.75, 2) + 0.9375;
        }

        return 7.5625 * Math.Pow(progress - 2.625 / 2.75, 2) + 0.984375;
    }

    /// <summary>按序号取缓动曲线（1 起）。</summary>
    public static double Curve(int type, double progress)
    {
        // 与参考 easing.mjs 的 curves[type - 1] 对齐：type=1 对应下面的 0 号曲线。
        return (type - 1) switch
        {
            0 => progress,
            1 => Math.Sin(progress * HalfPi),
            2 => 1 - Math.Cos(progress * HalfPi),
            3 => 1 - Math.Pow(1 - progress, 2),
            4 => progress * progress,
            5 => (1 - Math.Cos(Math.PI * progress)) / 2,
            6 => progress < 0.5 ? 2 * progress * progress : 1 - Math.Pow(-2 * progress + 2, 2) / 2,
            7 => 1 - Math.Pow(1 - progress, 3),
            8 => Math.Pow(progress, 3),
            9 => 1 - Math.Pow(1 - progress, 4),
            10 => Math.Pow(progress, 4),
            11 => progress < 0.5 ? 4 * Math.Pow(progress, 3) : 1 - Math.Pow(-2 * progress + 2, 3) / 2,
            12 => progress < 0.5 ? 8 * Math.Pow(progress, 4) : 1 - Math.Pow(-2 * progress + 2, 4) / 2,
            13 => 1 - Math.Pow(1 - progress, 5),
            14 => Math.Pow(progress, 5),
            15 => progress == 1 ? 1 : 1 - Math.Pow(2, -10 * progress),
            16 => progress == 0 ? 0 : Math.Pow(2, 10 * progress - 10),
            17 => Math.Sqrt(1 - Math.Pow(progress - 1, 2)),
            18 => 1 - Math.Sqrt(Math.Abs(1 - progress * progress)),
            19 => 1 + (Back + 1) * Math.Pow(progress - 1, 3) + Back * Math.Pow(progress - 1, 2),
            20 => (Back + 1) * Math.Pow(progress, 3) - Back * progress * progress,
            21 => progress < 0.5
                ? (1 - Math.Sqrt(1 - Math.Pow(2 * progress, 2))) / 2
                : (Math.Sqrt(1 - Math.Pow(-2 * progress + 2, 2)) + 1) / 2,
            22 => progress < 0.5
                ? Math.Pow(2 * progress, 2) * ((BackInOut + 1) * 2 * progress - BackInOut) / 2
                : (Math.Pow(2 * progress - 2, 2) * ((BackInOut + 1) * (2 * progress - 2) + BackInOut) + 2) / 2,
            23 => progress == 0 || progress == 1
                ? progress
                : Math.Pow(2, -10 * progress) * Math.Sin((progress * 10 - 0.75) * Elastic) + 1,
            24 => progress == 0 || progress == 1
                ? progress
                : -(Math.Pow(2, 10 * progress - 10)) * Math.Sin((progress * 10 - 10.75) * Elastic),
            25 => BounceOut(progress),
            26 => 1 - BounceOut(1 - progress),
            27 => progress < 0.5 ? (1 - BounceOut(1 - 2 * progress)) / 2 : (1 + BounceOut(2 * progress - 1)) / 2,
            28 => progress == 0 || progress == 1
                ? progress
                : progress < 0.5
                    ? -(Math.Pow(2, 20 * progress - 10) * Math.Sin((20 * progress - 11.125) * ElasticInOut)) / 2
                    : Math.Pow(2, -20 * progress + 10) * Math.Sin((20 * progress - 11.125) * ElasticInOut) / 2 + 1,
            _ => progress,
        };
    }

    /// <summary>在 [left, right] 区间上求缓动后的归一化值。</summary>
    public static double Evaluate(double progress, int type = 1, double left = 0, double right = 1)
    {
        var curveType = type is >= 1 and <= Count ? type : 1;
        var baseValue = Curve(curveType, left);
        var range = Curve(curveType, right) - baseValue;
        if (Math.Abs(range) < 1e-12)
        {
            return progress;
        }

        return (Curve(curveType, left + (right - left) * progress) - baseValue) / range;
    }

    /// <summary>贝塞尔缓动（四个控制点）。</summary>
    public static double Bezier(double progress, IReadOnlyList<double> points)
    {
        if (points.Count != 4 || points.Any(value => !double.IsFinite(value)))
        {
            return progress;
        }

        if (progress <= 0)
        {
            return 0;
        }

        if (progress >= 1)
        {
            return 1;
        }

        var (firstX, firstY, secondX, secondY) = (points[0], points[1], points[2], points[3]);

        static double Component(double parameter, double first, double second) =>
            3 * parameter * Math.Pow(1 - parameter, 2) * first +
            3 * parameter * parameter * (1 - parameter) * second +
            Math.Pow(parameter, 3);

        double parameter;
        if (firstX == 0 && secondX == 0)
        {
            parameter = Math.Cbrt(progress);
        }
        else if (firstX == 1 && secondX == 1)
        {
            parameter = 1 - Math.Cbrt(1 - progress);
        }
        else
        {
            var reverse = progress > 0.5;
            var target = reverse ? 1 - progress : progress;
            var first = reverse ? 1 - secondX : firstX;
            var second = reverse ? 1 - firstX : secondX;
            var low = 0.0;
            var high = 1.0;
            for (var iteration = 0; iteration < 40; iteration++)
            {
                var middle = (low + high) / 2;
                if (Component(middle, first, second) < target)
                {
                    low = middle;
                }
                else
                {
                    high = middle;
                }
            }

            parameter = reverse ? 1 - (low + high) / 2 : (low + high) / 2;
        }

        return parameter > 0.5
            ? 1 - Component(1 - parameter, 1 - secondY, 1 - firstY)
            : Component(parameter, firstY, secondY);
    }

    /// <summary>缓动名称（供界面下拉使用）。</summary>
    public static string DisplayName(int type) => type switch
    {
        1 => "线性",
        2 => "正弦",
        3 => "正弦（反向）",
        4 => "二次缓出",
        5 => "二次缓入",
        6 => "余弦",
        7 => "二次缓入缓出",
        8 => "三次缓出",
        9 => "三次缓入",
        10 => "四次缓出",
        11 => "四次缓入",
        12 => "三次缓入缓出",
        13 => "四次缓入缓出",
        14 => "五次缓出",
        15 => "五次缓入",
        16 => "指数缓出",
        17 => "指数缓入",
        18 => "圆形缓出",
        19 => "圆形缓入",
        20 => "回弹缓出",
        21 => "回弹缓入",
        22 => "圆形缓入缓出",
        23 => "回弹缓入缓出",
        24 => "弹性缓出",
        25 => "弹性缓入",
        26 => "弹跳缓出",
        27 => "弹跳缓入",
        28 => "弹跳缓入缓出",
        29 => "弹性缓入缓出",
        _ => "自定义",
    };
}

namespace PhiEdit.Core.Layout;

/// <summary>
/// 竖线网格（音符横向定位网格）。
/// 数量为奇数时正中会恰好落一条竖线，因此居中竖线会被加粗。
/// </summary>
public readonly struct VerticalGrid
{
    /// <summary>舞台横向半宽（RPE 舞台宽 1350）。</summary>
    public const double StageHalfWidth = 675.0;

    /// <summary>舞台全宽。</summary>
    public const double StageWidth = 1350.0;

    public VerticalGrid(int count)
    {
        Count = Math.Max(2, count);
        Spacing = StageWidth / (Count - 1);
        var half = (Math.Floor(Count + 0.01) - 1) / 2.0;
        First = -half;
        Last = half;
    }

    /// <summary>竖线总数。</summary>
    public int Count { get; }

    /// <summary>相邻竖线间距。</summary>
    public double Spacing { get; }

    /// <summary>最左侧竖线的索引偏移。</summary>
    public double First { get; }

    /// <summary>最右侧竖线的索引偏移。</summary>
    public double Last { get; }

    /// <summary>竖线数量是否为奇数（居中线存在）。</summary>
    public bool HasCenterLine => Count % 2 == 1;

    /// <summary>把横坐标吸附到最近的竖线上。</summary>
    public double Snap(double value)
    {
        var index = Math.Round(value / Spacing - First, MidpointRounding.AwayFromZero) + First;
        return index * Spacing;
    }

    /// <summary>枚举所有竖线横坐标。</summary>
    public IEnumerable<double> Lines()
    {
        for (var index = 0; index < Count; index++)
        {
            yield return (index + First) * Spacing;
        }
    }

    /// <summary>找出离给定坐标最近的竖线索引。</summary>
    public int NearestIndex(double value) =>
        (int)Math.Round(value / Spacing - First, MidpointRounding.AwayFromZero);
}

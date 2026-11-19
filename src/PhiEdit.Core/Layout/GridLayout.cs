namespace PhiEdit.Core.Layout;

/// <summary>网格刻度类别。</summary>
public enum GridTickKind
{
    /// <summary>整拍线：红色、加粗，标注拍号。</summary>
    Beat,

    /// <summary>细分线：黄色细线，位于整拍线之间。</summary>
    Subdivision,

    /// <summary>次级细分线：更淡的黄色细线。</summary>
    Minor,
}

/// <summary>一条网格刻度。</summary>
public readonly struct GridTick
{
    public GridTick(double beat, GridTickKind kind, int index)
    {
        Beat = beat;
        Kind = kind;
        Index = index;
    }

    public double Beat { get; }

    public GridTickKind Kind { get; }

    /// <summary>该拍内的细分序号。</summary>
    public int Index { get; }

    public bool IsBeatLine => Kind == GridTickKind.Beat;
}

/// <summary>
/// 网格刻度生成。横线数量由「每拍细分数」决定：相邻两根整拍线之间的细线数即几分之几拍。
/// </summary>
public static class GridLayout
{
    /// <summary>
    /// 生成 [firstBeat, lastBeat] 区间内的所有刻度。
    /// </summary>
    public static IEnumerable<GridTick> Ticks(double firstBeat, double lastBeat, int division)
    {
        division = Math.Max(1, division);
        if (firstBeat > lastBeat)
        {
            (firstBeat, lastBeat) = (lastBeat, firstBeat);
        }

        var startIndex = (int)Math.Floor(firstBeat * division);
        var endIndex = (int)Math.Ceiling(lastBeat * division);
        for (var index = startIndex; index <= endIndex; index++)
        {
            var beat = (double)index / division;
            if (beat < firstBeat - 1e-9 || beat > lastBeat + 1e-9)
            {
                continue;
            }

            var inBeat = index % division;
            if (inBeat == 0)
            {
                yield return new GridTick(beat, GridTickKind.Beat, index);
            }
            else
            {
                yield return new GridTick(beat, inBeat % 2 == 0 ? GridTickKind.Subdivision : GridTickKind.Minor, index);
            }
        }
    }
}

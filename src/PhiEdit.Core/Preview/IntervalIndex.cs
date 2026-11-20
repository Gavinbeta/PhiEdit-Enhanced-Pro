namespace PhiEdit.Core.Preview;

/// <summary>区间索引条目。</summary>
public sealed record IntervalEntry<T>(T Item, int Index, double Start, double End);

/// <summary>
/// 按起点排序、带前缀最大终点的区间索引。query 复杂度 O(log n + k)，
/// 对应参考源码 core/interval-index.mjs 的树形索引。
/// </summary>
public sealed class IntervalIndex<T>
{
    private readonly IntervalEntry<T>[] _entries;
    private readonly double[] _prefixMaxEnd;

    public IntervalIndex(IEnumerable<T> items, Func<T, double> start, Func<T, double> end)
    {
        _entries = items
            .Select((item, index) => new IntervalEntry<T>(item, index, start(item), end(item)))
            .OrderBy(entry => entry.Start)
            .ToArray();

        _prefixMaxEnd = new double[_entries.Length];
        var maximum = double.NegativeInfinity;
        for (var index = 0; index < _entries.Length; index++)
        {
            maximum = Math.Max(maximum, _entries[index].End);
            _prefixMaxEnd[index] = maximum;
        }
    }

    public IReadOnlyList<IntervalEntry<T>> Query(double start, double end)
    {
        if (_entries.Length == 0)
        {
            return Array.Empty<IntervalEntry<T>>();
        }

        var first = LowerBoundPrefixMax(start);
        if (first >= _entries.Length)
        {
            return Array.Empty<IntervalEntry<T>>();
        }

        var result = new List<IntervalEntry<T>>();
        for (var index = first; index < _entries.Length && _entries[index].Start <= end; index++)
        {
            if (_entries[index].End >= start)
            {
                result.Add(_entries[index]);
            }
        }

        return result;
    }

    /// <summary>第一个前缀最大终点不小于 value 的下标。</summary>
    private int LowerBoundPrefixMax(double value)
    {
        var low = 0;
        var high = _entries.Length;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (_prefixMaxEnd[middle] < value)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }
}

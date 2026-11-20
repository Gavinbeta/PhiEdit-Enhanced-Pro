using PhiEdit.Core.Beats;
using PhiEdit.Core.Charts;

namespace PhiEdit.Core.Preview;

/// <summary>
/// 速度事件积分。移植自参考源码 core/events.mjs 的 SpeedIntegral。
/// 参考实现中最终乘 120 完成 RPE 距离单位换算，漏掉会导致音符几乎不动。
/// </summary>
public sealed class SpeedIntegral
{
    private readonly EventTrack _track;
    private readonly List<Segment> _segments = new();
    private readonly double _firstTime;
    private readonly double _lastTime;
    private readonly double _lastDistance;
    private readonly double _zero;

    public SpeedIntegral(IEnumerable<LineEvent> events, TempoMap tempo, double factor = 1)
    {
        _track = new EventTrack(events, tempo, factor, 0);
        var entries = _track.Entries;
        var cuts = new SortedSet<double> { 0 };
        foreach (var entry in entries)
        {
            cuts.Add(entry.Start);
            cuts.Add(entry.End);
        }

        var values = cuts.ToList();
        var distance = 0.0;
        for (var index = 0; index < values.Count - 1; index++)
        {
            var start = values[index];
            var end = values[index + 1];
            var entry = EntryAt(entries, (start + end) / 2);
            var segment = new Segment(start, end, distance, entry);
            _segments.Add(segment);
            distance += Integrate(segment, end);
        }

        _firstTime = values[0];
        _lastTime = values[^1];
        _lastDistance = distance;
        _zero = Raw(0);
    }

    public double Value(double seconds) => _track.Number(seconds);

    public double Distance(double seconds) => (Raw(seconds) - _zero) * 120;

    private double Raw(double seconds)
    {
        if (seconds <= _firstTime)
        {
            return (seconds - _firstTime) * Value(_firstTime);
        }

        if (seconds >= _lastTime)
        {
            return _lastDistance + (seconds - _lastTime) * Value(_lastTime);
        }

        var index = Math.Max(0, UpperBound(_segments, seconds, segment => segment.Start) - 1);
        var segment = _segments[index];
        return segment.Distance + Integrate(segment, seconds);
    }

    private double Integrate(Segment segment, double end)
    {
        if (segment.Entry is null)
        {
            return 0;
        }

        var entry = segment.Entry;
        var duration = end - segment.Start;
        var item = entry.Event;
        if (item.Kind != EventTrackKind.Color && Math.Abs(item.StartValue - item.EndValue) < 1e-12)
        {
            return duration * item.StartValue;
        }

        if (segment.Start >= entry.End)
        {
            return duration * _track.Number(entry.End);
        }

        if (end <= entry.Start)
        {
            return duration * _track.Number(entry.Start);
        }

        if (!item.Bezier && item.Easing == 1)
        {
            return duration * (_track.Number(segment.Start) + _track.Number(end)) / 2;
        }

        var step = (end - segment.Start) / 20;
        var sum = 0.0;
        for (var index = 0; index <= 20; index++)
        {
            var weight = index is 0 or 20 ? 1 : index % 2 == 1 ? 4 : 2;
            sum += weight * _track.Number(segment.Start + step * index);
        }

        return sum * step / 3;
    }

    private static EventTrack.EventEntry? EntryAt(IReadOnlyList<EventTrack.EventEntry> entries, double seconds) =>
        entries.Count == 0 ? null : entries[Math.Max(0, UpperBound(entries, seconds, entry => entry.Start) - 1)];

    private static int UpperBound<T>(IReadOnlyList<T> values, double seconds, Func<T, double> selector)
    {
        var low = 0;
        var high = values.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (seconds < selector(values[middle]))
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }

        return low;
    }

    private sealed record Segment(double Start, double End, double Distance, EventTrack.EventEntry? Entry);
}

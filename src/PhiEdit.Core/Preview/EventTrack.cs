using PhiEdit.Core.Beats;
using PhiEdit.Core.Charts;
using EasingMath = PhiEdit.Core.Easing.Easing;

namespace PhiEdit.Core.Preview;

/// <summary>
/// 按秒采样事件轨道。移植自参考源码 core/events.mjs 的 EventTrack。
/// 支持数值、RGB 数组与文字事件；运行时状态只读取这里的结果。
/// </summary>
public sealed class EventTrack
{
    private readonly List<EventEntry> _events;
    private int _cursor;

    public EventTrack(IEnumerable<LineEvent> events, TempoMap tempo, double factor = 1, object? fallback = null)
    {
        Fallback = fallback;
        _events = events
            .Select(item => new EventEntry(item, tempo.SecondsAt(item.StartBeat.Value, factor), tempo.SecondsAt(item.EndBeat.Value, factor)))
            .OrderBy(entry => entry.Start)
            .ToList();
    }

    internal IReadOnlyList<EventEntry> Entries => _events;

    /// <summary>无事件时的默认数值。</summary>
    public object? Fallback { get; }

    /// <summary>是否有事件。</summary>
    public bool HasEvents => _events.Count > 0;

    /// <summary>
    /// 旧版本编辑器曾把 Alpha 写成 0–1；这类轨道需要在采样后放大到 0–255。
    /// 标准 RPE 事件本身已是 0–255，不会被这里改变。
    /// </summary>
    public bool IsLegacyAlpha => HasEvents && _events.All(entry =>
        Math.Abs(entry.Event.StartValue) <= 1.001 && Math.Abs(entry.Event.EndValue) <= 1.001);

    public double Number(double seconds) => ToDouble(Value(seconds));

    public object? Value(double seconds)
    {
        if (_events.Count == 0)
        {
            return Fallback;
        }

        var current = _events[_cursor];
        var next = _cursor + 1 < _events.Count ? _events[_cursor + 1] : null;
        if (!double.IsFinite(seconds) ||
            (!double.IsFinite(current.Start) && _cursor == 0) ||
            (seconds < current.Start && _cursor != 0) ||
            (next is not null && seconds >= next.Start))
        {
            _cursor = Math.Max(0, UpperBound(seconds) - 1);
        }

        return Sample(_events[_cursor], seconds);
    }

    internal object? Sample(EventEntry entry, double seconds)
    {
        var item = entry.Event;
        var progress = entry.End <= entry.Start
            ? 1
            : Math.Clamp((seconds - entry.Start) / (entry.End - entry.Start), 0, 1);
        var amount = EasingAmount(item, progress);

        if (item.Kind == EventTrackKind.Color)
        {
            var start = ParseRgb(item.Color);
            var end = ParseRgb(item.EndColor ?? item.Color);
            if (start.SequenceEqual(end))
            {
                return start;
            }

            return start.Select((value, index) => value + (end[index] - value) * amount).ToArray();
        }

        if (item.Kind == EventTrackKind.Text)
        {
            return FormatText(item.StartText ?? string.Empty, item.EndText ?? string.Empty, seconds, entry, amount);
        }

        if (Math.Abs(item.StartValue - item.EndValue) < 1e-12)
        {
            return item.StartValue;
        }

        return item.StartValue + (item.EndValue - item.StartValue) * amount;
    }

    private static double EasingAmount(LineEvent item, double progress)
    {
        if (item.Bezier)
        {
            return EasingMath.Bezier(progress, item.BezierPoints);
        }

        return EasingMath.Evaluate(progress, item.Easing, item.EasingLeft, item.EasingRight);
    }

    private static string FormatText(string start, string end, double seconds, EventEntry entry, double amount)
    {
        if (seconds < entry.Start)
        {
            return string.Empty;
        }

        if (seconds >= entry.End)
        {
            return end.Replace("%P%", string.Empty, StringComparison.Ordinal);
        }

        if (start.Contains("%P%", StringComparison.Ordinal) && end.Contains("%P%", StringComparison.Ordinal))
        {
            var first = double.TryParse(start.Replace("%P%", string.Empty, StringComparison.Ordinal), out var a);
            var second = double.TryParse(end.Replace("%P%", string.Empty, StringComparison.Ordinal), out var b);
            if (first && second)
            {
                var value = a + (b - a) * amount;
                return Math.Abs(a - Math.Truncate(a)) < 1e-9 && Math.Abs(b - Math.Truncate(b)) < 1e-9
                    ? Math.Truncate(value).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : value.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        if (end.StartsWith(start, StringComparison.Ordinal))
        {
            var suffix = end[start.Length..];
            return start + suffix[..(int)Math.Floor(suffix.Length * amount)];
        }

        if (start.StartsWith(end, StringComparison.Ordinal))
        {
            var suffix = start[end.Length..];
            return end + suffix[..(int)Math.Floor(suffix.Length * (1 - amount))];
        }

        return start;
    }

    public static double ToDouble(object? value) => value switch
    {
        double number => number,
        int number => number,
        float number => number,
        bool flag => flag ? 1 : 0,
        _ => 0,
    };

    public static double[] ParseRgb(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new[] { 255.0, 255.0, 255.0 };
        }

        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length == 3 && parts.All(part => double.TryParse(part, out _)))
        {
            return parts.Select(part => double.Parse(part, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        }

        return new[] { 255.0, 255.0, 255.0 };
    }

    private int UpperBound(double seconds)
    {
        var low = 0;
        var high = _events.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (seconds < _events[middle].Start)
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

    internal sealed record EventEntry(LineEvent Event, double Start, double End);
}

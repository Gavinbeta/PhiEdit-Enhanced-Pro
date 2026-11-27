using System.Text.Json;
using System.Text.Json.Nodes;
using PhiEdit.Core.Beats;
using PhiEdit.Core.Charts;

namespace PhiEdit.Core.IO;

/// <summary>
/// Phi/Phigros 旧版 formatVersion=3 谱面解析器。参考 rpe-next official-chart.mjs：
/// 只转换可识别内容，未知字段与噪声区域记录为提示后忽略。
/// </summary>
internal static class OfficialChartConverter
{
    private static readonly HashSet<string> RootKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "formatVersion", "offset", "judgeLineList", "blockAreaList",
    };

    private static readonly HashSet<string> LineKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "bpm", "notesAbove", "notesBelow", "speedEvents", "judgeLineMoveEvents",
        "judgeLineRotateEvents", "judgeLineDisappearEvents",
    };

    private static readonly HashSet<string> NoteKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "type", "time", "positionX", "holdTime", "speed", "floorPosition",
    };

    private static readonly HashSet<string> EventKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "startTime", "endTime", "value", "start", "end", "start2", "end2",
        "easingType", "easing", "easingLeft", "easingRight", "bezier", "bezierPoints",
    };

    public static bool IsOfficial(JsonElement root) =>
        root.TryGetProperty("formatVersion", out var version) &&
        version.ValueKind == JsonValueKind.Number &&
        Math.Abs(version.GetDouble() - 3) < 0.01 &&
        root.TryGetProperty("judgeLineList", out var lines) &&
        lines.ValueKind == JsonValueKind.Array &&
        lines.GetArrayLength() > 0;

    public static ChartReadResult Convert(JsonElement root, List<string> warnings)
    {
        WarnUnknown(root, RootKeys, "根节点", warnings);

        var linesElement = root.GetProperty("judgeLineList");
        var chart = new ChartDocument
        {
            RpeVersion = 170,
            OffsetMilliseconds = GetDouble(root, "offset", 0) * 1000,
        };

        var baseBpm = 120.0;
        if (linesElement.EnumerateArray().Any() &&
            linesElement[0].TryGetProperty("bpm", out var firstBpm) &&
            firstBpm.ValueKind == JsonValueKind.Number && firstBpm.GetDouble() > 0)
        {
            baseBpm = firstBpm.GetDouble();
        }
        else
        {
            warnings.Add("旧格式谱面缺少有效 bpm，已使用 120 BPM");
        }

        // ChartDocument 默认带一条 120 BPM @0 的点，必须先清空，
        // 否则与旧谱面的 baseBpm @0 重叠导致 TempoMap 拍秒换算不一致。
        chart.BpmList.Clear();
        chart.BpmList.Add(new TempoPoint(Beat.Zero, baseBpm));

        // 参考 official-chart.mjs：旧格式噪域必须转换成内部 blockAreaList 并保留，
        // 否则带噪域谱面的遮罩/反色效果缺失，画面与原版不符。
        OfficialNoiseConverter.Convert(root, chart, baseBpm, GetDouble(root, "offset", 0), warnings);

        var index = 0;
        foreach (var lineElement in linesElement.EnumerateArray())
        {
            try
            {
                chart.Lines.Add(ConvertLine(lineElement, index, baseBpm, warnings));
            }
            catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException or OverflowException)
            {
                warnings.Add($"第 {index + 1} 条判定线无法识别，已跳过：{exception.Message}");
            }

            index++;
        }

        if (chart.Lines.Count == 0)
        {
            warnings.Add("旧格式谱面没有可识别的判定线");
        }

        return new ChartReadResult(chart, warnings);
    }

    private static JudgeLine ConvertLine(JsonElement element, int index, double baseBpm, List<string> warnings)
    {
        var context = $"判定线 {index + 1}";
        WarnUnknown(element, LineKeys, context, warnings);
        var bpm = GetDouble(element, "bpm", baseBpm);
        var factor = bpm > 0 ? baseBpm / bpm : 1;
        var line = new JudgeLine
        {
            Id = index,
            Number = index + 1,
            BpmFactor = factor,
            IsCover = 1,
        };

        // 旧格式 speedEvents 的值字段是 value（不是 start/end）。参考 official-chart.mjs：e.value * 4.5。
        ConvertEvents(element, "speedEvents", "speed", line, context,
            value => value * 4.5, value => value * 4.5, warnings, "value", "value");
        ConvertEvents(element, "judgeLineDisappearEvents", "alpha", line, context,
            value => value * 255, value => value * 255, warnings);
        ConvertEvents(element, "judgeLineMoveEvents", "x", line, context,
            value => -675 + value * 1350, value => -675 + value * 1350, warnings, "start", "end");
        ConvertEvents(element, "judgeLineMoveEvents", "y", line, context,
            value => -450 + value * 900, value => -450 + value * 900, warnings, "start2", "end2");
        ConvertEvents(element, "judgeLineRotateEvents", "rotate", line, context,
            value => -value, value => -value, warnings);

        ConvertNotes(element, "notesAbove", true, line, context, warnings);
        ConvertNotes(element, "notesBelow", false, line, context, warnings);
        return line;
    }

    private static void ConvertEvents(JsonElement line, string sourceKey, string trackKey, JudgeLine target,
        string context, Func<double, double> startMap, Func<double, double> endMap,
        List<string> warnings, string startField = "start", string endField = "end")
    {
        if (!line.TryGetProperty(sourceKey, out var events) || events.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var kind = EventTrackKinds.FromKey(trackKey) ?? EventTrackKind.X;
        foreach (var item in events.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                warnings.Add($"{context} 的 {sourceKey} 含有无法识别的事件，已跳过");
                continue;
            }

            WarnUnknown(item, EventKeys, $"{context} {sourceKey}", warnings);
            var start = ToBeat(GetDouble(item, "startTime", 0));
            var end = ToBeat(GetDouble(item, "endTime", start.Value + 1));
            if (end.Value <= start.Value)
            {
                end = Beat.FromValue(start.Value + 1);
            }

            target.AddEvent(new LineEvent
            {
                Kind = kind,
                Layer = 0,
                StartBeat = start,
                EndBeat = end,
                StartValue = startMap(GetDouble(item, startField, 0)),
                EndValue = endMap(GetDouble(item, endField, 0)),
                Easing = (int)GetDouble(item, "easingType", GetDouble(item, "easing", 1)),
                EasingLeft = GetDouble(item, "easingLeft", 0),
                EasingRight = GetDouble(item, "easingRight", 1),
                Bezier = GetBool(item, "bezier"),
                BezierPoints = ReadBezier(item),
            });
        }
    }

    private static void ConvertNotes(JsonElement line, string key, bool above, JudgeLine target,
        string context, List<string> warnings)
    {
        if (!line.TryGetProperty(key, out var notes) || notes.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in notes.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                warnings.Add($"{context} 的 {key} 含有无法识别的音符，已跳过");
                continue;
            }

            WarnUnknown(item, NoteKeys, $"{context} {key}", warnings);
            var rawType = (int)GetDouble(item, "type", 1);
            var type = rawType switch
            {
                1 => NoteType.Tap,
                2 => NoteType.Drag,
                3 => NoteType.Hold,
                4 => NoteType.Flick,
                _ => (NoteType?)null,
            };
            if (type is null)
            {
                warnings.Add($"{context} 含有未知音符类型 {rawType}，已跳过");
                continue;
            }

            var beat = ToBeat(GetDouble(item, "time", 0));
            var hold = GetDouble(item, "holdTime", 0);
            var end = type == NoteType.Hold && hold > 0
                ? ToBeat(GetDouble(item, "time", 0) + hold)
                : beat;
            if (type == NoteType.Hold && end.Value <= beat.Value)
            {
                end = Beat.FromValue(beat.Value + 1);
            }

            target.Notes.Add(new Note
            {
                Type = type.Value,
                StartBeat = beat,
                EndBeat = end,
                PositionX = GetDouble(item, "positionX", 0) * 75,
                PositionY = 0,
                IsUp = above,
                Speed = type == NoteType.Hold ? 1 : GetDouble(item, "speed", 1),
                Size = 1,
                Alpha = 255,
                VisibleTime = 999999,
                IsFake = false,
                RawNode = JsonNode.Parse(item.GetRawText()) as JsonObject,
            });
        }
    }

    private static Beat ToBeat(double ticks) => Beat.FromValue(Math.Max(-100, Math.Floor(ticks + 0.1)) / 32.0);

    private static void WarnUnknown(JsonElement element, HashSet<string> known, string context, List<string> warnings)
    {
        if (element.ValueKind != JsonValueKind.Object || warnings.Count >= 40)
        {
            return;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (!known.Contains(property.Name))
            {
                warnings.Add($"{context} 已忽略未知字段：{property.Name}");
            }
        }
    }

    private static double GetDouble(JsonElement element, string property, double fallback = 0) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && double.IsFinite(value.GetDouble())
            ? value.GetDouble()
            : fallback;

    private static bool GetBool(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => value.GetDouble() != 0,
            _ => false,
        };

    private static double[] ReadBezier(JsonElement element)
    {
        if (!element.TryGetProperty("bezierPoints", out var points) || points.ValueKind != JsonValueKind.Array)
        {
            return new[] { 0.0, 0.0, 1.0, 1.0 };
        }

        var values = points.EnumerateArray().Take(4).Select(item =>
            item.ValueKind == JsonValueKind.Number ? item.GetDouble() : 0).ToArray();
        return values.Length == 4 ? values : new[] { 0.0, 0.0, 1.0, 1.0 };
    }
}

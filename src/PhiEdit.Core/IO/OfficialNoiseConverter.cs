using System.Text.Json;
using PhiEdit.Core.Beats;
using PhiEdit.Core.Charts;

namespace PhiEdit.Core.IO;

/// <summary>
/// 旧格式噪域（blockAreaList）转换器。严格对照 rpe-next core/official-noise.mjs：
/// convertOfficialNoiseAreas + convertTrack + frames。
/// 噪域是覆盖在音符之上的遮罩/反色区域，必须保留，否则带噪域谱面渲染异常。
/// </summary>
internal static class OfficialNoiseConverter
{
    /// <summary>参考 official-noise.mjs 的 easingTypes 数组。</summary>
    private static readonly int[] EasingTypes = { 1, 5, 4, 7, 9, 8, 12, 11, 10, 13, 15, 14 };

    /// <summary>秒 → 拍：(seconds - offset) * bpm / 60。参考 official-noise.mjs 的 beat()。</summary>
    private static Beat SecondsToBeat(double seconds, double bpm, double offset) =>
        Beat.FromValue((seconds - offset) * bpm / 60.0);

    private static NoisePoint Position(JsonElement element, string path, List<string> warnings)
    {
        var x = GetDouble(element, "x", double.NaN);
        var y = GetDouble(element, "y", double.NaN);
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            warnings.Add($"{path} 噪域坐标无效，已使用默认值");
            return new NoisePoint(0, 0);
        }
        return new NoisePoint(x * 1350 - 675, y * 900 - 450);
    }

    private sealed record Frame(double Time, double Value, int Ease, NoisePoint? Anchor);

    private static List<Frame> Frames(JsonElement source, string key, Func<JsonElement, string, double> readValue,
        bool anchored, string path, List<string> warnings, string? axis = null)
    {
        var result = new List<Frame>();
        if (!source.TryGetProperty(key, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        var index = 0;
        foreach (var eventElement in array.EnumerateArray())
        {
            var eventPath = $"{path}.{key}[{index++}]";
            if (eventElement.ValueKind != JsonValueKind.Object)
            {
                warnings.Add($"{eventPath} 噪域事件无法识别，已跳过");
                continue;
            }

            var ease = GetInt(eventElement, "easeType", -1);
            // 参考：event?.easeType ?? event?.['easeType' + axis] ?? 0
            if (ease < 0 && axis is not null)
            {
                ease = GetInt(eventElement, "easeType" + axis, 0);
            }
            if (ease < 0)
            {
                ease = 0;
            }
            if (ease > 14)
            {
                warnings.Add($"{eventPath} 噪域缓动类型 {ease} 超出范围，已按线性处理");
                ease = 0;
            }

            var time = GetDouble(eventElement, "time", double.NaN);
            if (!double.IsFinite(time))
            {
                warnings.Add($"{eventPath} 噪域事件时间无效，已跳过");
                continue;
            }

            NoisePoint? anchor = null;
            if (anchored && eventElement.TryGetProperty("anchor", out var anchorElement))
            {
                anchor = Position(anchorElement, eventPath + ".anchor", warnings);
            }

            result.Add(new Frame(time, readValue(eventElement, eventPath), ease, anchor));
        }

        result.Sort((a, b) => a.Time.CompareTo(b.Time));
        return result;
    }

    /// <summary>
    /// 把关键帧转换为事件轨道。对照 official-noise.mjs convertTrack。
    /// preserveInstant 用于 scale/rotate：保留瞬时跳变（transformStep）。
    /// </summary>
    private static void ConvertTrack(List<NoiseEvent> target, List<Frame> keyframes,
        double bpm, double offset, bool preserveInstant)
    {
        if (preserveInstant)
        {
            void AppendInstant(double start, double end, double startTime, double endTime, NoisePoint? anchor,
                int easingType = 1, double? transformStart = null, double? transformEnd = null, bool transformStep = false)
            {
                target.Add(new NoiseEvent
                {
                    Start = start,
                    End = end,
                    StartTime = SecondsToBeat(startTime, bpm, offset),
                    EndTime = SecondsToBeat(endTime, bpm, offset),
                    Anchor = anchor,
                    EasingType = easingType,
                    TransformStart = transformStart,
                    TransformEnd = transformEnd,
                    TransformStep = transformStep,
                });
            }

            for (var index = 1; index < keyframes.Count; index++)
            {
                var previous = keyframes[index - 1];
                var current = keyframes[index];
                if (Math.Abs(current.Time - previous.Time) < 1e-12)
                {
                    AppendInstant(current.Value, current.Value, current.Time, current.Time,
                        previous.Anchor ?? current.Anchor, 1, previous.Value, current.Value, true);
                }
                else if (previous.Ease == 13)
                {
                    AppendInstant(previous.Value, previous.Value, previous.Time, current.Time,
                        previous.Anchor, 1, previous.Value, current.Value);
                }
                else if (previous.Ease == 14)
                {
                    AppendInstant(current.Value, current.Value, previous.Time, current.Time,
                        previous.Anchor, 1, previous.Value, current.Value);
                }
                else if (previous.Ease == 12 && Math.Abs(previous.Value - current.Value) > 1e-12)
                {
                    var middleTime = (previous.Time + current.Time) / 2;
                    var middleValue = (previous.Value + current.Value) / 2;
                    AppendInstant(previous.Value, middleValue, previous.Time, middleTime, previous.Anchor, 15);
                    AppendInstant(middleValue, current.Value, middleTime, current.Time, previous.Anchor, 14,
                        middleValue, current.Value);
                }
                else
                {
                    AppendInstant(previous.Value, current.Value, previous.Time, current.Time,
                        previous.Anchor, EasingAt(previous.Ease));
                }
            }

            if (keyframes.Count > 0)
            {
                var final = keyframes[^1];
                var step = keyframes.Count > 1 && Math.Abs(keyframes[^2].Time - final.Time) < 1e-12;
                AppendInstant(final.Value, final.Value, final.Time, final.Time, final.Anchor, 1, null, null, step);
            }
            return;
        }

        // 非 preserveInstant：按时间分组（同刻取最后一个）
        var groups = new List<(double Time, Frame First, Frame Last)>();
        foreach (var frame in keyframes)
        {
            if (groups.Count > 0 && Math.Abs(groups[^1].Time - frame.Time) < 1e-12)
            {
                var last = groups[^1];
                groups[^1] = (last.Time, last.First, frame);
            }
            else
            {
                groups.Add((frame.Time, frame, frame));
            }
        }

        void Append(double start, double end, double startTime, double endTime, NoisePoint? anchor, int easingType = 1)
        {
            target.Add(new NoiseEvent
            {
                Start = start,
                End = end,
                StartTime = SecondsToBeat(startTime, bpm, offset),
                EndTime = SecondsToBeat(endTime, bpm, offset),
                Anchor = anchor,
                EasingType = easingType,
            });
        }

        for (var index = 1; index < groups.Count; index++)
        {
            var previous = groups[index - 1];
            var current = groups[index];
            var start = previous.Last.Value;
            var ease = previous.Last.Ease;
            var anchor = previous.Last.Anchor;
            var end = current.First.Value;
            if (ease == 13)
            {
                Append(start, start, previous.Time, current.Time, anchor);
            }
            else if (ease == 14)
            {
                Append(end, end, previous.Time, current.Time, anchor);
            }
            else if (ease == 12 && Math.Abs(start - end) > 1e-12)
            {
                var middleTime = (previous.Time + current.Time) / 2;
                var middleValue = (start + end) / 2;
                Append(start, middleValue, previous.Time, middleTime, anchor, 15);
                Append(middleValue, end, middleTime, current.Time, anchor, 14);
            }
            else
            {
                Append(start, end, previous.Time, current.Time, anchor, EasingAt(ease));
            }
        }

        if (groups.Count > 0)
        {
            var final = groups[^1].Last;
            Append(final.Value, final.Value, final.Time, final.Time, final.Anchor);
        }
    }

    private static int EasingAt(int ease) => ease >= 0 && ease < EasingTypes.Length ? EasingTypes[ease] : 1;

    /// <summary>转换全部噪域，返回是否含噪域。</summary>
    public static bool Convert(JsonElement root, ChartDocument chart, double baseBpm, double offset, List<string> warnings)
    {
        chart.BlockAreas.Clear();
        if (!root.TryGetProperty("blockAreaList", out var areas) || areas.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var index = 0;
        foreach (var entry in areas.EnumerateArray())
        {
            var path = $"blockAreaList[{index++}]";
            if (entry.ValueKind != JsonValueKind.Object)
            {
                warnings.Add($"{path} 噪域无法识别，已跳过");
                continue;
            }

            try
            {
                var area = new NoiseArea
                {
                    Name = GetString(entry, "Name") ?? string.Empty,
                    BottomLeft = Position(entry.GetProperty("bottomLeftPercentage"), path + ".bottomLeftPercentage", warnings),
                    TopRight = Position(entry.GetProperty("topRightPercentage"), path + ".topRightPercentage", warnings),
                    AppearTime = SecondsToBeat(GetDouble(entry, "appearTime", 0), baseBpm, offset),
                    DisappearTime = SecondsToBeat(GetDouble(entry, "disappearTime", 0), baseBpm, offset),
                    IsInvert = GetBool(entry, "isSubtract"),
                };

                var appear = GetDouble(entry, "appearTime", 0);
                var disappear = GetDouble(entry, "disappearTime", appear);
                var enable = Math.Max(appear, GetDouble(entry, "enableTime", appear));
                var disable = Math.Min(disappear, GetDouble(entry, "disableTime", disappear));
                if (disable > enable)
                {
                    area.ActiveIntervals.Add(new NoiseInterval
                    {
                        StartTime = SecondsToBeat(enable, baseBpm, offset),
                        EndTime = SecondsToBeat(disable, baseBpm, offset),
                        ReadyFlash = 0.5,
                    });
                }

                // moveEvents：endPosition 的 x/y。参考 ease 取 easeType ?? easeTypeX/easeTypeY ?? 0。
                var moveX = Frames(entry, "moveEvents",
                    (element, elementPath) => Position(element.GetProperty("endPosition"), elementPath + ".endPosition", warnings).X,
                    false, path, warnings, "X");
                var moveY = Frames(entry, "moveEvents",
                    (element, elementPath) => Position(element.GetProperty("endPosition"), elementPath + ".endPosition", warnings).Y,
                    false, path, warnings, "Y");
                ConvertTrack(area.MoveXEvents, moveX, baseBpm, offset, false);
                ConvertTrack(area.MoveYEvents, moveY, baseBpm, offset, false);

                // scaleEvents：scale.x / scale.y，preserveInstant = true
                var scaleX = new List<Frame>();
                var scaleY = new List<Frame>();
                if (entry.TryGetProperty("scaleEvents", out var scaleArray) && scaleArray.ValueKind == JsonValueKind.Array)
                {
                    var eventIndex = 0;
                    foreach (var eventElement in scaleArray.EnumerateArray())
                    {
                        var eventPath = $"{path}.scaleEvents[{eventIndex++}]";
                        var time = GetDouble(eventElement, "time", double.NaN);
                        if (!double.IsFinite(time) || !eventElement.TryGetProperty("anchor", out var anchorElement) ||
                            !eventElement.TryGetProperty("scale", out var scaleElement))
                        {
                            continue;
                        }
                        var anchor = Position(anchorElement, eventPath + ".anchor", warnings);
                        scaleX.Add(new Frame(time, GetDouble(scaleElement, "x", 1), GetInt(eventElement, "easeType", GetInt(eventElement, "easeTypeX", 0)), anchor));
                        scaleY.Add(new Frame(time, GetDouble(scaleElement, "y", 1), GetInt(eventElement, "easeType", GetInt(eventElement, "easeTypeY", 0)), anchor));
                    }
                    scaleX.Sort((a, b) => a.Time.CompareTo(b.Time));
                    scaleY.Sort((a, b) => a.Time.CompareTo(b.Time));
                }
                ConvertTrack(area.ScaleXEvents, scaleX, baseBpm, offset, true);
                ConvertTrack(area.ScaleYEvents, scaleY, baseBpm, offset, true);

                // rotateEvents：-rotation，preserveInstant = true
                var rotate = Frames(entry, "rotateEvents",
                    (element, elementPath) => -GetDouble(element, "rotation", 0), true, path, warnings);
                ConvertTrack(area.RotateEvents, rotate, baseBpm, offset, true);

                chart.BlockAreas.Add(area);
            }
            catch (Exception exception) when (exception is KeyNotFoundException or JsonException or FormatException or InvalidOperationException)
            {
                warnings.Add($"{path} 噪域无法识别，已跳过：{exception.Message}");
            }
        }

        if (chart.BlockAreas.Count > 0)
        {
            chart.IgnoreTripleInversion = true;
            // 参考：有噪域时 RPEVersion = NOISE_VERSION(220)；若有分层则为 230。旧格式转换统一 220。
            chart.RpeVersion = Math.Max(chart.RpeVersion, 220);
            warnings.Add($"已转换 {chart.BlockAreas.Count} 个噪域区域（blockAreaList）");
        }

        return chart.BlockAreas.Count > 0;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double GetDouble(JsonElement element, string property, double fallback = 0) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && double.IsFinite(value.GetDouble())
            ? value.GetDouble() : fallback;

    private static int GetInt(JsonElement element, string property, int fallback = 0) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? (int)value.GetDouble() : fallback;

    private static bool GetBool(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => value.GetDouble() != 0,
            _ => false,
        };
}

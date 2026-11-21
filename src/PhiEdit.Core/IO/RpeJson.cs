using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PhiEdit.Core.Beats;
using PhiEdit.Core.Charts;

namespace PhiEdit.Core.IO;

/// <summary>
/// RPE JSON 谱面读写。
///
/// 写出格式与 PhiEdit-Base / rpe-next 的 chart.mjs 保持一致：
/// META + BPMList + judgeLineGroup + judgeLineList，标准事件位于 line.eventLayers 数组，
/// 扩展事件位于 line.extended。读入同时兼容本程序早期写出过的平铺旧格式。
/// </summary>
public static class RpeJson
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    private static readonly string[] LegacyRootKeys =
    {
        "formatVersion", "RPEVersion", "songName", "songArtist", "chartName", "difficulty",
        "songFilename", "songDuration", "beatMap", "eventGroups", "events",
    };

    private static readonly string[] LegacyLineKeys =
    {
        "id", "time", "eventCount", "isFake", "judgeLineNumber", "x", "y", "rotate",
        "alpha", "speed", "customBackground", "color", "texture", "vlines", "notes",
        "events", "eventLayers", "extended",
    };

    public static ChartDocument Read(string json)
    {
        var text = json.TrimStart('\uFEFF');
        using var parsed = JsonDocument.Parse(text);
        var root = parsed.RootElement;
        var hasMeta = root.TryGetProperty("META", out var meta) && meta.ValueKind == JsonValueKind.Object;
        var metaElement = hasMeta ? meta : root;

        var chart = new ChartDocument
        {
            SongName = GetString(metaElement, "name") ?? GetString(root, "songName") ?? "Untitled",
            SongArtist = GetString(metaElement, "composer") ?? GetString(root, "songArtist") ?? string.Empty,
            ChartName = GetString(metaElement, "charter") ?? GetString(root, "chartName") ?? "Default",
            Difficulty = GetString(metaElement, "level") ?? GetString(root, "difficulty") ?? string.Empty,
            SongFilename = GetString(metaElement, "song") ?? GetString(root, "songFilename") ?? string.Empty,
            Background = GetString(metaElement, "background") ?? string.Empty,
            OffsetMilliseconds = GetDouble(metaElement, "offset", GetDouble(root, "offset")),
            SongDuration = hasMeta ? 0 : GetDouble(root, "songDuration"),
            RpeVersion = (int)GetDouble(metaElement, "RPEVersion", GetDouble(root, "RPEVersion", 170)),
            RawRoot = JsonNode.Parse(text) as JsonObject,
        };

        chart.BpmList.Clear();
        if (root.TryGetProperty("BPMList", out var bpmList) && bpmList.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in bpmList.EnumerateArray())
            {
                var beat = ReadBeat(entry, "startTime");
                var bpm = GetDouble(entry, "bpm", 120);
                if (bpm > 0)
                {
                    chart.BpmList.Add(new TempoPoint(beat, bpm));
                }
            }
        }

        if (chart.BpmList.Count == 0)
        {
            chart.BpmList.Add(new TempoPoint(Beat.Zero, 120));
        }

        if (root.TryGetProperty("judgeLineList", out var lines) && lines.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var lineElement in lines.EnumerateArray())
            {
                chart.Lines.Add(ReadLine(lineElement, index));
                index++;
            }
        }

        return chart;
    }

    public static string Write(ChartDocument chart)
    {
        var root = chart.RawRoot?.DeepClone() as JsonObject ?? new JsonObject();
        foreach (var key in LegacyRootKeys)
        {
            root.Remove(key);
        }

        root["META"] = new JsonObject
        {
            ["RPEVersion"] = Math.Max(170, chart.RpeVersion),
            ["name"] = chart.SongName,
            ["composer"] = chart.SongArtist,
            ["charter"] = chart.ChartName,
            ["illustration"] = string.Empty,
            ["level"] = chart.Difficulty,
            ["song"] = chart.SongFilename,
            ["background"] = chart.Background,
            ["offset"] = chart.OffsetMilliseconds,
        };

        var bpmArray = new JsonArray();
        foreach (var point in chart.BpmList)
        {
            bpmArray.Add(new JsonObject
            {
                ["startTime"] = WriteBeat(point.StartBeat),
                ["bpm"] = point.Bpm,
            });
        }

        root["BPMList"] = bpmArray;

        var groups = root["judgeLineGroup"] as JsonArray;
        if (groups is null || groups.Count == 0)
        {
            groups = new JsonArray("Default");
        }

        root["judgeLineGroup"] = groups;

        var lineArray = new JsonArray();
        foreach (var line in chart.Lines)
        {
            lineArray.Add(WriteLine(line));
        }

        root["judgeLineList"] = lineArray;
        return root.ToJsonString(WriteOptions);
    }

    private static JsonObject WriteLine(JudgeLine line)
    {
        var node = line.RawNode?.DeepClone() as JsonObject ?? new JsonObject();
        foreach (var key in LegacyLineKeys)
        {
            node.Remove(key);
        }

        node["Name"] = $"判定线 {line.Number}";
        node["Group"] = 0;
        node["Texture"] = string.IsNullOrEmpty(line.Texture) ? "line.png" : line.Texture;
        node["bpmfactor"] = line.BpmFactor;
        node["father"] = line.Father;
        node["rotateWithFather"] = line.RotateWithFather ?? true;
        node["isCover"] = line.IsCover;
        node["zOrder"] = line.ZOrder;
        node["anchor"] = new JsonArray(line.AnchorX, line.AnchorY);
        node["isGif"] = false;
        node["numOfNotes"] = line.Notes.Count;
        node["attachUI"] = line.AttachUI;

        WriteControl(node, "alphaControl", "alpha", line.AlphaControl);
        WriteControl(node, "posControl", "pos", line.PosControl);
        WriteControl(node, "sizeControl", "size", line.SizeControl);
        WriteControl(node, "skewControl", "skew", line.SkewControl);
        WriteControl(node, "yControl", "y", line.YControl);

        var noteArray = new JsonArray();
        foreach (var note in line.Notes)
        {
            var noteNode = note.RawNode?.DeepClone() as JsonObject ?? new JsonObject();
            noteNode["type"] = (int)note.Type;
            noteNode["startTime"] = WriteBeat(note.StartBeat);
            noteNode["endTime"] = WriteBeat(note.Type == NoteType.Hold ? note.EndBeat : note.StartBeat);
            noteNode["positionX"] = note.PositionX;
            noteNode["yOffset"] = note.PositionY;
            noteNode["above"] = note.IsUp ? 1 : 0;
            noteNode["isFake"] = note.IsFake ? 1 : 0;
            noteNode["speed"] = note.Speed;
            noteNode["size"] = note.Size;
            noteNode["visibleTime"] = note.VisibleTime;
            noteNode["alpha"] = note.Alpha;
            if (note.Color is { Length: 3 })
            {
                noteNode["color"] = new JsonArray(note.Color[0], note.Color[1], note.Color[2]);
            }
            if (note.Tint is { Length: 3 })
            {
                noteNode["tint"] = new JsonArray(note.Tint[0], note.Tint[1], note.Tint[2]);
            }
            if (note.TintHitEffects is { Length: 3 })
            {
                noteNode["tintHitEffects"] = new JsonArray(note.TintHitEffects[0], note.TintHitEffects[1], note.TintHitEffects[2]);
            }
            noteArray.Add(noteNode);
        }

        node["notes"] = noteArray;

        var layers = new JsonArray();
        var layer0 = CreateEmptyStandardLayer();
        foreach (var item in line.Events.Where(item => EventTrackKinds.Standard.Contains(item.Kind)))
        {
            AddEvent(layer0, item);
        }

        layers.Add(layer0);
        foreach (var layerIndex in line.Layers.Keys.Where(index => index > 0 && index != JudgeLine.SpecialLayer).Order())
        {
            var layer = CreateEmptyStandardLayer();
            foreach (var track in line.Layers[layerIndex])
            {
                if (!EventTrackKinds.Standard.Contains(track.Key))
                {
                    continue;
                }

                foreach (var item in track.Value)
                {
                    AddEvent(layer, item);
                }
            }

            layers.Add(layer);
        }

        node["eventLayers"] = layers;

        var extended = new JsonObject();
        foreach (var item in line.Events.Where(item => EventTrackKinds.Extended.Contains(item.Kind) || item.Kind == EventTrackKind.Incline))
        {
            AddEvent(extended, item);
        }

        foreach (var layerIndex in line.Layers.Keys.Where(index => index > 0 && index != JudgeLine.SpecialLayer).Order())
        {
            foreach (var item in line.Layers[layerIndex].SelectMany(track => track.Value)
                         .Where(item => item.Kind == EventTrackKind.Incline))
            {
                AddEvent(extended, item);
            }
        }

        if (line.Layers.TryGetValue(JudgeLine.SpecialLayer, out var special))
        {
            foreach (var track in special)
            {
                foreach (var item in track.Value)
                {
                    AddEvent(extended, item);
                }
            }
        }

        node["extended"] = extended;
        return node;
    }

    private static void WriteControl(JsonObject node, string name, string valueKey, IReadOnlyList<ControlPoint> points)
    {
        var array = new JsonArray();
        foreach (var point in points)
        {
            array.Add(new JsonObject
            {
                ["x"] = point.X,
                [valueKey] = point.Y,
                ["easing"] = point.Easing,
            });
        }

        node[name] = array;
    }

    private static JsonObject CreateEmptyStandardLayer()
    {
        var layer = new JsonObject();
        foreach (var kind in EventTrackKinds.Standard)
        {
            layer[kind.Key()] = new JsonArray();
        }

        return layer;
    }

    private static void AddEvent(JsonObject layer, LineEvent item)
    {
        if (layer[item.Kind.Key()] is not JsonArray array)
        {
            array = new JsonArray();
            layer[item.Kind.Key()] = array;
        }

        array.Add(WriteEvent(item));
    }

    private static JsonObject WriteEvent(LineEvent item)
    {
        var node = item.RawNode?.DeepClone() as JsonObject ?? new JsonObject();
        node["startTime"] = WriteBeat(item.StartBeat);
        node["endTime"] = WriteBeat(item.EndBeat);

        if (item.Kind == EventTrackKind.Color)
        {
            node["start"] = ParseRgb(item.Color);
            node["end"] = ParseRgb(item.EndColor ?? item.Color);
        }
        else if (item.Kind == EventTrackKind.Text)
        {
            node["start"] = item.StartText ?? string.Empty;
            node["end"] = item.EndText ?? string.Empty;
        }
        else
        {
            node["start"] = item.StartValue;
            node["end"] = item.EndValue;
        }

        node["easingType"] = Math.Max(1, item.Easing);
        node["easingLeft"] = item.EasingLeft;
        node["easingRight"] = item.EasingRight;
        node["bezier"] = item.Bezier ? 1 : 0;
        node["bezierPoints"] = new JsonArray(
            item.BezierPoints.ElementAtOrDefault(0),
            item.BezierPoints.ElementAtOrDefault(1),
            item.BezierPoints.ElementAtOrDefault(2),
            item.BezierPoints.ElementAtOrDefault(3));
        node["linkgroup"] = item.LinkedGroup;
        // 扩展字段：参考程序会原样透传，本程序用它恢复“钩定”状态。
        node["inst"] = item.IsHooked ? 1 : 0;

        if (item.Kind == EventTrackKind.Paint)
        {
            node["shader"] = item.ShaderType ?? string.Empty;
            node["line"] = item.ShaderOrder;
            node["vars"] = new JsonObject { [item.ShaderParameter ?? "sampleCount"] = item.StartValue };
        }

        return node;
    }

    private static JsonArray ParseRgb(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new JsonArray(255, 255, 255);
        }

        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length == 3 && parts.All(part => int.TryParse(part, out _)))
        {
            return new JsonArray(int.Parse(parts[0], CultureInfo.InvariantCulture),
                int.Parse(parts[1], CultureInfo.InvariantCulture),
                int.Parse(parts[2], CultureInfo.InvariantCulture));
        }

        return new JsonArray(255, 255, 255);
    }

    private static JudgeLine ReadLine(JsonElement element, int index)
    {
        var line = new JudgeLine
        {
            Id = (int)GetDouble(element, "id", index),
            Number = (int)GetDouble(element, "judgeLineNumber", index),
            Time = ReadBeat(element, "time", "startTime"),
            X = GetDouble(element, "x"),
            Y = GetDouble(element, "y"),
            Rotate = GetDouble(element, "rotate"),
            Alpha = GetDouble(element, "alpha", 255) / 255.0,
            Speed = GetDouble(element, "speed", 1),
            Father = (int)GetDouble(element, "father", -1),
            ZOrder = (int)GetDouble(element, "zOrder"),
            BpmFactor = GetPositive(element, "bpmfactor", 1),
            RotateWithFather = element.TryGetProperty("rotateWithFather", out _) ? GetBool(element, "rotateWithFather") : null,
            IsCover = (int)GetDouble(element, "isCover", 1),
            AnchorX = ReadAnchor(element, 0, 0.5),
            AnchorY = ReadAnchor(element, 1, 0.5),
            AttachUI = GetBool(element, "attachUI"),
            IsFake = GetBool(element, "isFake"),
            Texture = GetString(element, "Texture") ?? GetString(element, "texture"),
            Color = GetString(element, "color"),
            CustomBackground = GetString(element, "customBackground"),
            RawNode = JsonNode.Parse(element.GetRawText()) as JsonObject,
        };

        ReadControl(line.AlphaControl, element, "alphaControl", "alpha");
        ReadControl(line.PosControl, element, "posControl", "pos");
        ReadControl(line.SizeControl, element, "sizeControl", "size");
        ReadControl(line.SkewControl, element, "skewControl", "skew");
        ReadControl(line.YControl, element, "yControl", "y");

        if (element.TryGetProperty("notes", out var notes) && notes.ValueKind == JsonValueKind.Array)
        {
            foreach (var noteElement in notes.EnumerateArray())
            {
                var type = (NoteType)Math.Clamp((int)GetDouble(noteElement, "type", 1), 1, 4);
                var start = ReadBeat(noteElement, "startTime", "time");
                var hold = ReadBeat(noteElement, "endTime", "holdTime");
                line.Notes.Add(new Note
                {
                    Type = type,
                    StartBeat = start,
                    EndBeat = type == NoteType.Hold
                        ? (hold.Value > start.Value ? hold : Beat.FromValue(start.Value + 1))
                        : start,
                    PositionX = GetDouble(noteElement, "positionX"),
                    PositionY = GetDouble(noteElement, "yOffset", GetDouble(noteElement, "positionY")),
                    IsFake = GetBool(noteElement, "isFake"),
                    IsUp = ReadAbove(noteElement),
                    Speed = GetDouble(noteElement, "speed", 1),
                    Size = GetDouble(noteElement, "size", 1),
                    VisibleTime = GetDouble(noteElement, "visibleTime", GetDouble(noteElement, "visualTime", 999999)),
                    Alpha = GetDouble(noteElement, "alpha", 255),
                    Color = ReadRgb(noteElement, "color"),
                    Tint = ReadRgb(noteElement, "tint"),
                    TintHitEffects = ReadRgb(noteElement, "tintHitEffects"),
                    RawNode = JsonNode.Parse(noteElement.GetRawText()) as JsonObject,
                });
            }
        }

        // 新格式：eventLayers 是数组，extended 是独立对象。
        if (element.TryGetProperty("eventLayers", out var layerValue))
        {
            if (layerValue.ValueKind == JsonValueKind.Array)
            {
                var layerIndex = 0;
                foreach (var layer in layerValue.EnumerateArray())
                {
                    if (layer.ValueKind == JsonValueKind.Object)
                    {
                        ReadEventLayer(line, layer, layerIndex);
                    }

                    layerIndex++;
                }
            }
            else if (layerValue.ValueKind == JsonValueKind.Object)
            {
                // 兼容本程序早期写出的对象形式。
                foreach (var property in layerValue.EnumerateObject())
                {
                    if (int.TryParse(property.Name, out var layerIndex) && property.Value.ValueKind == JsonValueKind.Object)
                    {
                        ReadEventLayer(line, property.Value, layerIndex);
                    }
                }
            }
        }

        if (element.TryGetProperty("extended", out var extended) && extended.ValueKind == JsonValueKind.Object)
        {
            ReadEventLayer(line, extended, JudgeLine.SpecialLayer);
        }

        // 兼容早期平铺 events 对象。
        if (element.TryGetProperty("events", out var legacyEvents) && legacyEvents.ValueKind == JsonValueKind.Object)
        {
            ReadEventLayer(line, legacyEvents, 0);
        }

        return line;
    }

    private static void ReadEventLayer(JudgeLine line, JsonElement layer, int layerIndex)
    {
        foreach (var trackProperty in layer.EnumerateObject())
        {
            var kind = EventTrackKinds.FromKey(trackProperty.Name);
            if (kind is null || trackProperty.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var eventElement in trackProperty.Value.EnumerateArray())
            {
                line.AddEvent(ReadEvent(eventElement, kind.Value, layerIndex));
            }
        }
    }

    private static LineEvent ReadEvent(JsonElement element, EventTrackKind kind, int layer)
    {
        var start = ReadBeat(element, "startTime");
        var end = ReadBeat(element, "endTime");
        if (end.Value <= start.Value)
        {
            end = Beat.FromValue(start.Value + 1);
        }

        var startValue = GetDouble(element, "start");
        var endValue = GetDouble(element, "end");
        var color = GetString(element, "startColor");
        var endColor = GetString(element, "endColor");
        var startText = GetString(element, "startText");
        var endText = GetString(element, "endText");

        if (kind == EventTrackKind.Color && element.TryGetProperty("start", out var colorValue) && colorValue.ValueKind == JsonValueKind.Array)
        {
            color = FormatRgb(colorValue);
            endColor = element.TryGetProperty("end", out var endColorValue) && endColorValue.ValueKind == JsonValueKind.Array
                ? FormatRgb(endColorValue)
                : color;
        }

        if (kind == EventTrackKind.Text)
        {
            startText = GetString(element, "start") ?? startText;
            endText = GetString(element, "end") ?? endText;
        }

        return new LineEvent
        {
            Kind = kind,
            Layer = layer,
            StartBeat = start,
            EndBeat = end,
            StartValue = startValue,
            EndValue = endValue,
            LinkedGroup = (int)GetDouble(element, "linkgroup"),
            Easing = (int)GetDouble(element, "easingType", GetDouble(element, "easing", 1)),
            EasingLeft = GetDouble(element, "easingLeft"),
            EasingRight = GetDouble(element, "easingRight", 1),
            Bezier = GetBool(element, "bezier"),
            BezierPoints = ReadBezier(element),
            IsHooked = GetDouble(element, "inst") == 1,
            Text = GetString(element, "text"),
            Color = color ?? GetString(element, "color"),
            EndColor = endColor,
            StartText = startText,
            EndText = endText,
            ShaderType = GetString(element, "shader") ?? GetString(element, "shaderType") ?? "chromatic",
            ShaderGlobal = GetBool(element, "shaderGlobal"),
            ShaderAutoAlign = !element.TryGetProperty("shaderAutoAlign", out _) || GetBool(element, "shaderAutoAlign"),
            ShaderOrder = (int)GetDouble(element, "line", GetDouble(element, "shaderOrder")),
            ShaderParameter = GetString(element, "shaderParameter") ?? "sampleCount",
            ShaderCode = GetString(element, "shaderCode"),
            RawNode = JsonNode.Parse(element.GetRawText()) as JsonObject,
        };
    }

    private static void ReadControl(List<ControlPoint> target, JsonElement element, string property, string valueKey)
    {
        target.Clear();
        if (!element.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var point in array.EnumerateArray())
        {
            if (point.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var value = GetDouble(point, valueKey, GetDouble(point, "y", 1));
            target.Add(new ControlPoint(GetDouble(point, "x"), value, (int)GetDouble(point, "easing", 1)));
        }
    }

    private static double ReadAnchor(JsonElement element, int index, double fallback)
    {
        if (!element.TryGetProperty("anchor", out var anchor) || anchor.ValueKind != JsonValueKind.Array)
        {
            return fallback;
        }

        var values = anchor.EnumerateArray().ToArray();
        return index < values.Length && values[index].ValueKind == JsonValueKind.Number
            ? values[index].GetDouble()
            : fallback;
    }

    private static double GetPositive(JsonElement element, string property, double fallback) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.GetDouble() > 0
            ? value.GetDouble()
            : fallback;

    private static double[]? ReadRgb(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.Number ? item.GetDouble() : 0).Take(3).ToArray()
            : null;

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

    private static string FormatRgb(JsonElement value)
    {
        var parts = value.EnumerateArray().Select(item => item.GetDouble()).ToArray();
        return parts.Length == 3
            ? string.Join(", ", parts.Select(part => Math.Clamp((int)part, 0, 255).ToString(CultureInfo.InvariantCulture)))
            : "255, 255, 255";
    }

    private static bool ReadAbove(JsonElement element)
    {
        if (element.TryGetProperty("above", out var above))
        {
            return GetBool(above);
        }

        return !element.TryGetProperty("up", out var up) || GetBool(up);
    }

    private static Beat ReadBeat(JsonElement element, params string[] properties)
    {
        foreach (var property in properties)
        {
            if (!element.TryGetProperty(property, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Array)
            {
                var parts = value.EnumerateArray().Select(item => (int)item.GetDouble()).ToArray();
                if (parts.Length >= 3 && parts[2] > 0)
                {
                    return new Beat(parts[0], parts[1], parts[2]);
                }

                return Beat.Zero;
            }

            if (value.ValueKind == JsonValueKind.Number)
            {
                return Beat.FromValue(value.GetDouble());
            }
        }

        return Beat.Zero;
    }

    private static JsonArray WriteBeat(Beat beat) => new()
    {
        beat.Integer,
        beat.Numerator,
        beat.Denominator,
    };

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double GetDouble(JsonElement element, string property, double fallback = 0) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : fallback;

    private static bool GetBool(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && GetBool(value);

    private static bool GetBool(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.Number => value.GetDouble() != 0,
        JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) && parsed,
        _ => false,
    };
}

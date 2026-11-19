using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PhiEdit.Core.Beats;
using PhiEdit.Core.Charts;

namespace PhiEdit.Core.IO;

/// <summary>
/// RPE JSON 谱面读写。保持与原版字段命名一致，确保可与既有谱面互转。
/// </summary>
public static class RpeJson
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    public static ChartDocument Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var chart = new ChartDocument
        {
            SongName = GetString(root, "songName") ?? "Untitled",
            SongArtist = GetString(root, "songArtist") ?? string.Empty,
            ChartName = GetString(root, "chartName") ?? "Default",
            Difficulty = GetString(root, "difficulty") ?? string.Empty,
            SongFilename = GetString(root, "songFilename") ?? string.Empty,
            SongDuration = GetDouble(root, "songDuration"),
            RpeVersion = (int)GetDouble(root, "RPEVersion", 240),
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
        var root = new JsonObject
        {
            ["formatVersion"] = 1,
            ["RPEVersion"] = chart.RpeVersion,
            ["songName"] = chart.SongName,
            ["songArtist"] = chart.SongArtist,
            ["chartName"] = chart.ChartName,
            ["difficulty"] = chart.Difficulty,
            ["songFilename"] = chart.SongFilename,
            ["songDuration"] = chart.SongDuration,
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

        var tempo = chart.BuildTempoMap();
        var beatMap = new JsonObject();
        foreach (var point in chart.BpmList)
        {
            beatMap[point.StartBeat.ToString()] = new JsonArray
            {
                new JsonObject
                {
                    ["beat"] = WriteBeat(point.StartBeat),
                    ["time"] = Math.Round(tempo.SecondsAt(point.StartBeat.Value), 6),
                },
            };
        }

        root["beatMap"] = beatMap;

        var lineArray = new JsonArray();
        foreach (var line in chart.Lines)
        {
            lineArray.Add(WriteLine(line));
        }

        root["judgeLineList"] = lineArray;
        root["judgeLineGroup"] = new JsonObject();
        root["eventGroups"] = new JsonArray();

        return root.ToJsonString(WriteOptions);
    }

    private static JsonObject WriteLine(JudgeLine line)
    {
        var node = new JsonObject
        {
            ["id"] = line.Id,
            ["time"] = WriteBeat(line.Time),
            ["eventCount"] = line.Track(EventTrackKind.X).Count + line.Track(EventTrackKind.Y).Count +
                            line.Track(EventTrackKind.Rotate).Count + line.Track(EventTrackKind.Alpha).Count +
                            line.Track(EventTrackKind.Speed).Count,
            ["isFake"] = line.IsFake,
            ["judgeLineNumber"] = line.Number,
            ["x"] = line.X,
            ["y"] = line.Y,
            ["rotate"] = line.Rotate,
            ["alpha"] = line.Alpha,
            ["speed"] = line.Speed,
            ["customBackground"] = line.CustomBackground ?? string.Empty,
            ["color"] = line.Color ?? string.Empty,
            ["texture"] = line.Texture ?? string.Empty,
            ["vlines"] = new JsonArray(),
        };

        var noteArray = new JsonArray();
        foreach (var note in line.Notes)
        {
            noteArray.Add(new JsonObject
            {
                ["type"] = (int)note.Type,
                ["time"] = WriteBeat(note.StartBeat),
                ["positionX"] = note.PositionX,
                ["positionY"] = note.PositionY,
                ["holdTime"] = WriteBeat(note.EndBeat),
                ["isFake"] = note.IsFake,
                ["judgeLineNumber"] = line.Number,
            });
        }

        node["notes"] = noteArray;

        var events = new JsonObject();
        foreach (var kind in EventTrackKinds.Standard.Concat(EventTrackKinds.Extended))
        {
            var list = line.Track(kind);
            if (list.Count == 0)
            {
                continue;
            }

            var array = new JsonArray();
            foreach (var item in list)
            {
                var entry = new JsonObject
                {
                    ["startTime"] = WriteBeat(item.StartBeat),
                    ["endTime"] = WriteBeat(item.EndBeat),
                    ["start"] = item.StartValue,
                    ["end"] = item.EndValue,
                    ["linkgroup"] = item.LinkedGroup,
                    ["curveType"] = 0,
                    ["easing"] = item.Easing,
                    ["inst"] = item.IsHooked ? 1 : 0,
                };

                if (!string.IsNullOrEmpty(item.Text))
                {
                    entry["text"] = item.Text;
                }

                if (!string.IsNullOrEmpty(item.Color))
                {
                    entry["color"] = item.Color;
                }

                array.Add(entry);
            }

            events[kind.Key()] = array;
        }

        node["events"] = events;

        if (line.Layers.Count > 0)
        {
            var layerObject = new JsonObject();
            foreach (var layer in line.Layers)
            {
                var tracks = new JsonObject();
                foreach (var track in layer.Value)
                {
                    var array = new JsonArray();
                    foreach (var item in track.Value)
                    {
                        array.Add(new JsonObject
                        {
                            ["startTime"] = WriteBeat(item.StartBeat),
                            ["endTime"] = WriteBeat(item.EndBeat),
                            ["start"] = item.StartValue,
                            ["end"] = item.EndValue,
                            ["linkgroup"] = item.LinkedGroup,
                            ["curveType"] = 0,
                            ["easing"] = item.Easing,
                        });
                    }

                    tracks[track.Key.Key()] = array;
                }

                layerObject[layer.Key.ToString(CultureInfo.InvariantCulture)] = tracks;
            }

            node["eventLayers"] = layerObject;
        }

        return node;
    }

    private static JudgeLine ReadLine(JsonElement element, int index)
    {
        var line = new JudgeLine
        {
            Id = (int)GetDouble(element, "id", index),
            Number = (int)GetDouble(element, "judgeLineNumber", index),
            Time = ReadBeat(element, "time"),
            X = GetDouble(element, "x"),
            Y = GetDouble(element, "y"),
            Rotate = GetDouble(element, "rotate"),
            Alpha = GetDouble(element, "alpha", 1),
            Speed = GetDouble(element, "speed", 1),
            IsFake = GetBool(element, "isFake"),
            Texture = GetString(element, "texture"),
            Color = GetString(element, "color"),
            CustomBackground = GetString(element, "customBackground"),
        };

        if (element.TryGetProperty("notes", out var notes) && notes.ValueKind == JsonValueKind.Array)
        {
            foreach (var noteElement in notes.EnumerateArray())
            {
                var type = (NoteType)(int)GetDouble(noteElement, "type", 1);
                var start = ReadBeat(noteElement, "time");
                var hold = ReadBeat(noteElement, "holdTime");
                line.Notes.Add(new Note
                {
                    Type = type,
                    StartBeat = start,
                    EndBeat = type == NoteType.Hold ? hold : start,
                    PositionX = GetDouble(noteElement, "positionX"),
                    PositionY = GetDouble(noteElement, "positionY"),
                    IsFake = GetBool(noteElement, "isFake"),
                });
            }
        }

        if (element.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in events.EnumerateObject())
            {
                var kind = EventTrackKinds.FromKey(property.Name);
                if (kind is null || property.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var eventElement in property.Value.EnumerateArray())
                {
                    line.AddEvent(ReadEvent(eventElement, kind.Value, 0));
                }
            }
        }

        if (element.TryGetProperty("eventLayers", out var layerObject) && layerObject.ValueKind == JsonValueKind.Object)
        {
            foreach (var layerProperty in layerObject.EnumerateObject())
            {
                if (!int.TryParse(layerProperty.Name, out var layerIndex))
                {
                    continue;
                }

                if (layerProperty.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var trackProperty in layerProperty.Value.EnumerateObject())
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
        }

        return line;
    }

    private static LineEvent ReadEvent(JsonElement element, EventTrackKind kind, int layer)
    {
        var start = ReadBeat(element, "startTime");
        var end = ReadBeat(element, "endTime");
        return new LineEvent
        {
            Kind = kind,
            Layer = layer,
            StartBeat = start,
            EndBeat = end,
            StartValue = GetDouble(element, "start"),
            EndValue = GetDouble(element, "end"),
            LinkedGroup = (int)GetDouble(element, "linkgroup"),
            Easing = (int)GetDouble(element, "easing"),
            IsHooked = GetDouble(element, "inst") == 1,
            Text = GetString(element, "text"),
            Color = GetString(element, "color"),
        };
    }

    private static Beat ReadBeat(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return Beat.Zero;
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
        element.TryGetProperty(property, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.Number && value.GetDouble() != 0;
}

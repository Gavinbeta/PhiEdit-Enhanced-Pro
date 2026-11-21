using PhiEdit.Core.Beats;
using PhiEdit.Core.Charts;
using PhiEdit.Core.IO;
using Xunit;

namespace PhiEdit.Core.Tests;

public class RpeJsonTests
{
    [Fact]
    public void WriteThenRead_PreservesChartStructure()
    {
        var chart = new ChartDocument
        {
            SongName = "测试歌曲",
            SongArtist = "测试作者",
            ChartName = "Expert",
            Difficulty = "12",
            Background = "Resources/bg.png",
            OffsetMilliseconds = 20,
        };

        var line = chart.AddLine();
        line.Notes.Add(new Note
        {
            Type = NoteType.Flick,
            StartBeat = Beat.FromValue(4.5),
            PositionX = -120.5,
            Tint = new[] { 10.0, 20.0, 30.0 },
            TintHitEffects = new[] { 40.0, 50.0, 60.0 },
        });

        line.AddEvent(new LineEvent
        {
            Kind = EventTrackKind.Rotate,
            StartBeat = Beat.FromValue(2),
            EndBeat = Beat.FromValue(3),
            StartValue = 15,
            EndValue = 45,
            Easing = 6,
            EasingLeft = 0.2,
            EasingRight = 0.8,
            IsHooked = true,
        });

        var json = RpeJson.Write(chart);
        var restored = RpeJson.Read(json);

        Assert.Equal("测试歌曲", restored.SongName);
        Assert.Equal("测试作者", restored.SongArtist);
        Assert.Equal("Expert", restored.ChartName);
        Assert.Equal("Resources/bg.png", restored.Background);
        Assert.Equal(20, restored.OffsetMilliseconds, 6);
        Assert.Single(restored.Lines);

        var restoredLine = restored.Lines[0];
        Assert.Single(restoredLine.Notes);
        Assert.Equal(NoteType.Flick, restoredLine.Notes[0].Type);
        Assert.Equal(4.5, restoredLine.Notes[0].StartBeat.Value, 6);
        Assert.Equal(-120.5, restoredLine.Notes[0].PositionX, 6);
        Assert.Equal(new[] { 10.0, 20.0, 30.0 }, restoredLine.Notes[0].Tint);
        Assert.Equal(new[] { 40.0, 50.0, 60.0 }, restoredLine.Notes[0].TintHitEffects);

        var rotate = restoredLine.Track(EventTrackKind.Rotate);
        Assert.Single(rotate);
        Assert.Equal(15, rotate[0].StartValue, 6);
        Assert.Equal(45, rotate[0].EndValue, 6);
        Assert.Equal(6, rotate[0].Easing);
        Assert.Equal(0.2, rotate[0].EasingLeft, 6);
        Assert.Equal(0.8, rotate[0].EasingRight, 6);
        Assert.True(rotate[0].IsHooked);
    }

    [Fact]
    public void WriteThenRead_PreservesSpecialEventLayer()
    {
        var chart = new ChartDocument();
        var line = chart.AddLine();
        line.AddEvent(new LineEvent
        {
            Kind = EventTrackKind.Alpha,
            Layer = JudgeLine.SpecialLayer,
            StartBeat = Beat.FromValue(1),
            EndBeat = Beat.FromValue(2),
            StartValue = 0,
            EndValue = 255,
        });

        var json = RpeJson.Write(chart);
        Assert.Contains("\"extended\"", json);

        var restored = RpeJson.Read(json);
        var extended = restored.Lines[0].Track(EventTrackKind.Alpha, JudgeLine.SpecialLayer);
        Assert.Single(extended);
        Assert.Equal(255, extended[0].EndValue, 6);
    }

    [Fact]
    public void Write_UsesReferenceChartFormat()
    {
        var chart = new ChartDocument();
        var line = chart.AddLine();
        line.Notes.Add(new Note { Type = NoteType.Tap, StartBeat = Beat.FromValue(1), PositionX = 20 });
        line.AddEvent(new LineEvent
        {
            Kind = EventTrackKind.X,
            StartBeat = Beat.FromValue(0),
            EndBeat = Beat.FromValue(1),
            Easing = 6,
        });

        var json = RpeJson.Write(chart);
        Assert.Contains("\"META\"", json);
        Assert.Contains("\"eventLayers\"", json);
        Assert.Contains("\"moveXEvents\"", json);
        Assert.Contains("\"easingType\"", json);
        Assert.DoesNotContain("\"formatVersion\"", json);
        Assert.DoesNotContain("\"holdTime\"", json);
    }

    [Fact]
    public void Read_NewFormatAndPreserveUnknownFields()
    {
        const string json = """
        {
          "META": { "RPEVersion": 170, "name": "Fixture", "offset": 0 },
          "BPMList": [{ "bpm": 120, "startTime": [0, 0, 1] }],
          "judgeLineList": [{
            "Name": "Line", "eventLayers": [{
              "alphaEvents": [{ "startTime": [0,0,1], "endTime": [1,0,1], "start": 255, "end": 255, "easingType": 1 }]
            }],
            "notes": [{ "type": 1, "startTime": [2,0,1], "endTime": [2,0,1], "positionX": 10, "above": 1, "alpha": 255 }]
          }],
          "unknownExtension": { "preserve": true }
        }
        """;

        var chart = RpeJson.Read(json);
        Assert.Single(chart.Lines);
        Assert.Single(chart.Lines[0].Notes);
        Assert.Single(chart.Lines[0].Track(EventTrackKind.Alpha));

        var written = RpeJson.Write(chart);
        Assert.Contains("\"unknownExtension\"", written);
        Assert.Contains("\"alphaEvents\"", written);
    }

    [Fact]
    public void Read_AboveTwoMeansBelowAndRoundTrips()
    {
        const string json = """
        {
          "META": { "RPEVersion": 170 },
          "BPMList": [{ "bpm": 120, "startTime": [0, 0, 1] }],
          "judgeLineList": [{
            "eventLayers": [{}],
            "notes": [{ "type": 4, "startTime": [2,0,1], "endTime": [2,0,1], "positionX": 0, "above": 2 }]
          }]
        }
        """;

        var chart = RpeJson.Read(json);
        Assert.False(chart.Lines[0].Notes[0].IsUp);

        var written = RpeJson.Write(chart);
        Assert.Contains("\"above\": 2", written);
    }

    [Fact]
    public void Read_ToleratesMissingFields()
    {
        var chart = RpeJson.Read("{\"judgeLineList\":[{\"notes\":[{\"type\":1}]}]}");
        Assert.Single(chart.Lines);
        Assert.Single(chart.Lines[0].Notes);
        Assert.Equal(NoteType.Tap, chart.Lines[0].Notes[0].Type);
    }
}

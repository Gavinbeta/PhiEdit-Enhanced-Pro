using System.Text.Json;
using PhiEdit.Core.Charts;
using PhiEdit.Core.IO;
using Xunit;

namespace PhiEdit.Core.Tests;

public class OfficialChartImportTests
{
    [Fact]
    public void ReadWithWarnings_ConvertsOfficialChartAndReportsIgnoredFields()
    {
        const string json = """
        {
          "formatVersion": 3,
          "offset": 0.5,
          "judgeLineList": [{
            "bpm": 120,
            "notesAbove": [{ "type": 1, "time": 3200, "positionX": 0.5, "holdTime": 0, "speed": 1, "floorPosition": 0, "unknown": 1 }],
            "notesBelow": [{ "type": 3, "time": 6400, "positionX": -0.5, "holdTime": 960, "speed": 1, "floorPosition": 0 }],
            "speedEvents": [{ "startTime": 0, "endTime": 32, "value": 2 }],
            "judgeLineMoveEvents": [{ "startTime": 0, "endTime": 32, "start": 0.5, "end": 0.5, "start2": 2, "end2": 2 }],
            "judgeLineRotateEvents": [{ "startTime": 0, "endTime": 32, "start": 0, "end": 10 }],
            "judgeLineDisappearEvents": [{ "startTime": 0, "endTime": 32, "start": 0, "end": 1 }]
          }],
          "blockAreaList": [{ "startTime": 0, "endTime": 10 }],
          "weirdRoot": true
        }
        """;

        var result = RpeJson.ReadWithWarnings(json);
        Assert.Single(result.Chart.Lines);
        Assert.Equal(2, result.Chart.Lines[0].Notes.Count);
        Assert.Equal(500, result.Chart.OffsetMilliseconds);
        Assert.Contains(result.Warnings, warning => warning.Contains("blockAreaList"));
        Assert.Contains(result.Warnings, warning => warning.Contains("weirdRoot"));
        Assert.Contains(result.Warnings, warning => warning.Contains("unknown"));
        Assert.Equal(NoteType.Tap, result.Chart.Lines[0].Notes[0].Type);
        Assert.Equal(NoteType.Hold, result.Chart.Lines[0].Notes[1].Type);
        Assert.True(result.Chart.Lines[0].Notes[0].IsUp);
        Assert.False(result.Chart.Lines[0].Notes[1].IsUp);
    }

    [Fact]
    public void ReadWithWarnings_ConvertsSpeedEventValueField()
    {
        // 旧格式 speedEvents 的值字段是 value（不是 start/end）。漏读会导致速度全 0，
        // 音符挤在判定线上、Hold 长度为 0。参考 official-chart.mjs：e.value * 4.5。
        const string json = """
        {
          "formatVersion": 3,
          "judgeLineList": [{
            "bpm": 120,
            "speedEvents": [{ "startTime": 0, "endTime": 32, "value": 2 }],
            "notesAbove": [{ "type": 1, "time": 0, "positionX": 0 }]
          }]
        }
        """;

        var result = RpeJson.ReadWithWarnings(json);
        var speeds = result.Chart.Lines[0].Events.Where(e => e.Kind == EventTrackKind.Speed).ToList();
        Assert.Single(speeds);
        Assert.Equal(9, speeds[0].StartValue, 6);  // 2 * 4.5
        Assert.Equal(9, speeds[0].EndValue, 6);
    }

    [Fact]
    public void ReadWithWarnings_SkipsUnknownNotesAndStillParsesRest()
    {
        const string json = """
        {
          "formatVersion": 3,
          "judgeLineList": [{
            "bpm": 120,
            "notesAbove": [
              { "type": 99, "time": 0, "positionX": 0, "holdTime": 0 },
              { "type": 1, "time": 3200, "positionX": 0, "holdTime": 0 }
            ]
          }]
        }
        """;

        var result = RpeJson.ReadWithWarnings(json);
        Assert.Single(result.Chart.Lines[0].Notes);
        Assert.Contains(result.Warnings, warning => warning.Contains("未知音符类型"));
    }

    [Fact]
    public void ReadWithWarnings_ConvertsNoiseAreas()
    {
        const string json = """
        {
          "formatVersion": 3,
          "offset": 0,
          "judgeLineList": [{ "bpm": 120, "notesAbove": [{ "type": 1, "time": 0, "positionX": 0 }] }],
          "blockAreaList": [{
            "bottomLeftPercentage": { "x": 0.4, "y": 0.4 },
            "topRightPercentage": { "x": 0.6, "y": 0.6 },
            "appearTime": 0,
            "disappearTime": 2,
            "enableTime": 0,
            "disableTime": 1,
            "isSubtract": true,
            "moveEvents": [{ "time": 0, "endPosition": { "x": 0.5, "y": 0.5 }, "easeTypeX": 0, "easeTypeY": 0 }],
            "scaleEvents": [{ "time": 0, "anchor": { "x": 0.5, "y": 0.5 }, "scale": { "x": 1, "y": 1 }, "easeTypeX": 0, "easeTypeY": 0 }],
            "rotateEvents": [{ "time": 0, "anchor": { "x": 0.5, "y": 0.5 }, "rotation": 0, "easeType": 0 }]
          }]
        }
        """;

        var result = RpeJson.ReadWithWarnings(json);
        var chart = result.Chart;
        Assert.Single(chart.BlockAreas);
        Assert.True(chart.IgnoreTripleInversion);
        Assert.True(chart.BlockAreas[0].IsInvert);
        // 0.4*1350-675 = -135, 0.6*1350-675 = 135
        Assert.Equal(-135, chart.BlockAreas[0].BottomLeft.X, 6);
        Assert.Equal(135, chart.BlockAreas[0].TopRight.X, 6);
        // 2 秒 * 120 BPM / 60 = 4 拍
        Assert.Equal(4, chart.BlockAreas[0].DisappearTime.Value, 6);
        Assert.Single(chart.BlockAreas[0].ActiveIntervals);
        Assert.True(chart.RpeVersion >= 220);
        Assert.Contains(result.Warnings, warning => warning.Contains("噪域"));
    }
}

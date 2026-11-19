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
        };

        var line = chart.AddLine();
        line.Notes.Add(new Note
        {
            Type = NoteType.Flick,
            StartBeat = Beat.FromValue(4.5),
            PositionX = -120.5,
        });

        line.AddEvent(new LineEvent
        {
            Kind = EventTrackKind.Rotate,
            StartBeat = Beat.FromValue(2),
            EndBeat = Beat.FromValue(3),
            StartValue = 15,
            EndValue = 45,
        });

        var json = RpeJson.Write(chart);
        var restored = RpeJson.Read(json);

        Assert.Equal("测试歌曲", restored.SongName);
        Assert.Equal("测试作者", restored.SongArtist);
        Assert.Equal("Expert", restored.ChartName);
        Assert.Single(restored.Lines);

        var restoredLine = restored.Lines[0];
        Assert.Single(restoredLine.Notes);
        Assert.Equal(NoteType.Flick, restoredLine.Notes[0].Type);
        Assert.Equal(4.5, restoredLine.Notes[0].StartBeat.Value, 6);
        Assert.Equal(-120.5, restoredLine.Notes[0].PositionX, 6);

        var rotate = restoredLine.Track(EventTrackKind.Rotate);
        Assert.Single(rotate);
        Assert.Equal(15, rotate[0].StartValue, 6);
        Assert.Equal(45, rotate[0].EndValue, 6);
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

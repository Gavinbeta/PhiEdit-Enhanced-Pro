using PhiEdit.Core.Beats;
using PhiEdit.Core.Charts;
using PhiEdit.Core.Preview;
using Xunit;

namespace PhiEdit.Core.Tests;

public class PreviewRuntimeTests
{
    [Fact]
    public void SceneRuntime_SamplesEveryLine_IrrespectiveOfEditorSelection()
    {
        var chart = new ChartDocument();
        var first = chart.AddLine();
        var second = chart.AddLine();
        second.Notes.Add(new Note { Type = NoteType.Tap, StartBeat = Beat.FromValue(4), PositionX = 120 });
        var tempo = chart.BuildTempoMap();
        var scene = new SceneRuntime();

        scene.Compile(chart, tempo);
        var states = scene.Sample(tempo.SecondsAt(2));

        Assert.Equal(2, states.Count);
        Assert.Equal(2, scene.Lines.Count);
        Assert.Contains(scene.Lines[1].Notes, entry => entry.Note.PositionX == 120);
    }

    [Fact]
    public void SpeedIntegral_AppliesReferenceUnitScale()
    {
        var chart = new ChartDocument();
        var line = chart.AddLine();
        line.AddEvent(new LineEvent
        {
            Kind = EventTrackKind.Speed,
            StartBeat = Beat.Zero,
            EndBeat = Beat.FromValue(100),
            StartValue = 10,
            EndValue = 10,
        });
        var tempo = chart.BuildTempoMap();
        var runtime = new LineRuntime(line, tempo);

        // 10 单位/秒 × 120 换算系数 = 1200 距离/秒。
        Assert.Equal(1200, runtime.Floor(1), 6);
    }

    [Fact]
    public void Note_DistanceShrinksAsTimeAdvances()
    {
        var chart = new ChartDocument();
        var line = chart.AddLine();
        line.AddEvent(new LineEvent
        {
            Kind = EventTrackKind.Speed,
            StartBeat = Beat.Zero,
            EndBeat = Beat.FromValue(100),
            StartValue = 10,
            EndValue = 10,
        });
        line.Notes.Add(new Note { Type = NoteType.Tap, StartBeat = Beat.FromValue(4), PositionX = 0 });
        var tempo = chart.BuildTempoMap();
        var runtime = new LineRuntime(line, tempo);
        var early = tempo.SecondsAt(1);
        var late = tempo.SecondsAt(2);

        var earlyState = runtime.State(early);
        var lateState = runtime.State(late);
        var earlyNote = runtime.NoteState(runtime.Notes[0], earlyState, early);
        var lateNote = runtime.NoteState(runtime.Notes[0], lateState, late);

        Assert.True(earlyNote.Y > 0);
        Assert.True(lateNote.Y > 0);
        Assert.True(lateNote.Y < earlyNote.Y);
    }
}

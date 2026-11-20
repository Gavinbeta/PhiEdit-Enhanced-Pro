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
    public void LineAlpha_ComesFromAlphaEvents_NotBaseAlpha()
    {
        var chart = new ChartDocument();
        var line = chart.AddLine();
        line.Alpha = 1;
        line.AddEvent(new LineEvent
        {
            Kind = EventTrackKind.Alpha,
            StartBeat = Beat.Zero,
            EndBeat = Beat.FromValue(100),
            StartValue = 0,
            EndValue = 0,
        });
        var runtime = new LineRuntime(line, chart.BuildTempoMap());

        Assert.Equal(0, runtime.State(1).Alpha, 6);
    }

    [Fact]
    public void LineAlpha_KeepsLegacyNormalizedEventsReadable()
    {
        var chart = new ChartDocument();
        var line = chart.AddLine();
        line.AddEvent(new LineEvent
        {
            Kind = EventTrackKind.Alpha,
            StartBeat = Beat.Zero,
            EndBeat = Beat.FromValue(100),
            StartValue = 1,
            EndValue = 1,
        });
        var runtime = new LineRuntime(line, chart.BuildTempoMap());

        Assert.Equal(255, runtime.State(1).Alpha, 6);
    }

    [Fact]
    public void VisibleNotes_StillReturnsNotes_WhenLineAlphaIsZero()
    {
        var chart = new ChartDocument();
        var line = chart.AddLine();
        line.AddEvent(new LineEvent
        {
            Kind = EventTrackKind.Alpha,
            StartBeat = Beat.Zero,
            EndBeat = Beat.FromValue(100),
            StartValue = 0,
            EndValue = 0,
        });
        line.AddEvent(new LineEvent
        {
            Kind = EventTrackKind.Speed,
            StartBeat = Beat.Zero,
            EndBeat = Beat.FromValue(100),
            StartValue = 10,
            EndValue = 10,
        });
        var note = new Note { Type = NoteType.Tap, StartBeat = Beat.FromValue(4), PositionX = 0 };
        note.EndBeat = note.StartBeat;
        line.Notes.Add(note);
        var runtime = new LineRuntime(line, chart.BuildTempoMap());
        var state = runtime.State(chart.BuildTempoMap().SecondsAt(2));

        Assert.Equal(0, state.Alpha, 6);
        Assert.NotEmpty(runtime.VisibleNotes(chart.BuildTempoMap().SecondsAt(2), state, 1e9));
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

    [Fact]
    public void IntervalIndex_QueryReturnsOverlappingIntervals()
    {
        var index = new IntervalIndex<string>(
            new[] { "a", "b", "c" },
            item => item switch { "a" => 0, "b" => 5, _ => 10 },
            item => item switch { "a" => 4, "b" => 6, _ => 20 });

        var hit = index.Query(3, 5).Select(entry => entry.Item).ToList();
        Assert.Equal(new[] { "a", "b" }, hit);
        Assert.Empty(index.Query(7, 9));
    }

    [Fact]
    public void StaticNote_StaysVisible_WhenLineFloorDrifts()
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
        var staticNote = new Note { Type = NoteType.Tap, StartBeat = Beat.FromValue(4), Speed = 0, PositionX = 0 };
        staticNote.EndBeat = staticNote.StartBeat;
        line.Notes.Add(staticNote);
        var tempo = chart.BuildTempoMap();
        var runtime = new LineRuntime(line, tempo);
        var seconds = tempo.SecondsAt(2);
        var state = runtime.State(seconds);

        Assert.True(runtime.Floor(seconds) > 1000);
        Assert.Contains(staticNote, runtime.VisibleNotes(seconds, state).Select(entry => entry.Note));
    }

    [Fact]
    public void RecentHits_SkipsFakeNotes_AndDoesNotEmitHoldEndPulse()
    {
        var chart = new ChartDocument();
        var line = chart.AddLine();
        var hold = new Note { Type = NoteType.Hold, StartBeat = Beat.Zero, EndBeat = Beat.FromValue(1) };
        var fake = new Note { Type = NoteType.Tap, StartBeat = Beat.FromValue(0.25), IsFake = true };
        fake.EndBeat = fake.StartBeat;
        line.Notes.Add(hold);
        line.Notes.Add(fake);
        var runtime = new LineRuntime(line, chart.BuildTempoMap());

        var hits = runtime.RecentHits(1, 10);
        Assert.Equal(3, hits.Count);
        Assert.All(hits, hit => Assert.Same(hold, hit.Entry.Note));
        Assert.DoesNotContain(hits, hit => Math.Abs(hit.Time - runtime.Notes[0].End) < 1e-9);
    }
}

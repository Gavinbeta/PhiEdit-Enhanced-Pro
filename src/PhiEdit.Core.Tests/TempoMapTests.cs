using PhiEdit.Core.Beats;
using Xunit;

namespace PhiEdit.Core.Tests;

public class TempoMapTests
{
    [Fact]
    public void SingleTempo_ConvertsBeatsToSeconds()
    {
        var tempo = new TempoMap(new[] { new TempoPoint(Beat.Zero, 120) });
        Assert.Equal(2.0, tempo.SecondsAt(4.0), 6);
        Assert.Equal(4.0, tempo.BeatAt(2.0), 6);
    }

    [Fact]
    public void VariableTempo_SplitsAtChangePoint()
    {
        var tempo = new TempoMap(new[]
        {
            new TempoPoint(Beat.Zero, 120),
            new TempoPoint(Beat.FromValue(4), 60),
        });

        Assert.Equal(2.0, tempo.SecondsAt(4.0), 6);
        // 4 拍之后每拍 1 秒
        Assert.Equal(3.0, tempo.SecondsAt(5.0), 6);
        Assert.Equal(5.0, tempo.BeatAt(3.0), 6);
    }

    [Fact]
    public void BpmAt_ReportsActiveTempo()
    {
        var tempo = new TempoMap(new[]
        {
            new TempoPoint(Beat.Zero, 120),
            new TempoPoint(Beat.FromValue(8), 240),
        });

        Assert.Equal(120, tempo.BpmAt(2.0), 6);
        Assert.Equal(240, tempo.BpmAt(9.0), 6);
    }
}

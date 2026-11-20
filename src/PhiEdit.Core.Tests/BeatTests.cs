using PhiEdit.Core.Beats;
using Xunit;

namespace PhiEdit.Core.Tests;

public class BeatTests
{
    [Fact]
    public void Value_CombinesIntegerAndFraction()
    {
        var beat = new Beat(2, 1, 4);
        Assert.Equal(2.25, beat.Value, 6);
    }

    [Fact]
    public void FromValue_RoundTripsThroughParse()
    {
        var beat = Beat.FromValue(4.375);
        Assert.Equal(4.375, beat.Value, 6);
        Assert.Equal(beat, Beat.Parse(beat.ToString()));
    }

    [Theory]
    [InlineData("0:1/4", 0.25)]
    [InlineData("3:0/1", 3.0)]
    [InlineData("5", 5.0)]
    [InlineData("1/2", 0.5)]
    [InlineData("1.5", 1.5)]
    public void Parse_HandlesCommonFormats(string text, double expected)
    {
        Assert.Equal(expected, Beat.Parse(text).Value, 6);
    }

    [Fact]
    public void Snap_AlignsToDivision()
    {
        Assert.Equal(2.25, Beat.Snap(2.3, 4).Value, 6);
        Assert.Equal(2.5, Beat.Snap(2.4, 4).Value, 6);
    }

    [Fact]
    public void Simplified_ReducesFraction()
    {
        var beat = new Beat(1, 2, 4);
        Assert.Equal(new Beat(1, 1, 2), beat.Simplified());
    }
}

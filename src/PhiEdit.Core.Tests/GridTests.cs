using PhiEdit.Core.Layout;
using Xunit;

namespace PhiEdit.Core.Tests;

public class GridTests
{
    [Theory]
    [InlineData(21, 67.5)]
    [InlineData(11, 135)]
    [InlineData(2, 1350)]
    public void VerticalGrid_SpacingFollowsStageWidth(int count, double expected)
    {
        var grid = new VerticalGrid(count);
        Assert.Equal(expected, grid.Spacing, 6);
    }

    [Fact]
    public void VerticalGrid_OddCountHasCenterLine()
    {
        var grid = new VerticalGrid(21);
        Assert.True(grid.HasCenterLine);
        Assert.Contains(0d, grid.Lines());
    }

    [Fact]
    public void VerticalGrid_EvenCountHasNoCenterLine()
    {
        var grid = new VerticalGrid(20);
        Assert.False(grid.HasCenterLine);
        Assert.DoesNotContain(0d, grid.Lines());
    }

    [Fact]
    public void VerticalGrid_SnapsToNearestLane()
    {
        var grid = new VerticalGrid(21);
        Assert.Equal(67.5, grid.Snap(70), 6);
        Assert.Equal(-67.5, grid.Snap(-70), 6);
    }

    [Fact]
    public void GridLayout_ProducesBeatLinesAndSubdivisions()
    {
        var ticks = GridLayout.Ticks(0, 1, 4).ToList();
        Assert.Equal(5, ticks.Count);
        Assert.Equal(GridTickKind.Beat, ticks[0].Kind);
        Assert.Equal(GridTickKind.Minor, ticks[1].Kind);
        Assert.Equal(GridTickKind.Subdivision, ticks[2].Kind);
        Assert.Equal(GridTickKind.Minor, ticks[3].Kind);
        Assert.Equal(GridTickKind.Beat, ticks[4].Kind);
    }

    [Fact]
    public void GridLayout_BeatLinesSitOnIntegers()
    {
        foreach (var tick in GridLayout.Ticks(-2, 6, 4))
        {
            if (tick.Kind == GridTickKind.Beat)
            {
                Assert.Equal(Math.Round(tick.Beat), tick.Beat, 6);
            }
        }
    }
}

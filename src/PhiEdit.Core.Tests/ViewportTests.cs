using PhiEdit.Core.Layout;
using Xunit;

namespace PhiEdit.Core.Tests;

public class ViewportTests
{
    private static EditViewport Create(double height = 600, double inset = 26)
    {
        var viewport = new EditViewport
        {
            ViewportHeight = height,
            PlayheadInset = inset,
            PixelsPerBeat = 100,
        };

        viewport.SetPosition(4);
        return viewport;
    }

    [Fact]
    public void LaterBeatsAreDrawnHigher()
    {
        var viewport = Create();
        Assert.True(viewport.YAtBeat(5) < viewport.YAtBeat(4));
        Assert.True(viewport.YAtBeat(4) < viewport.YAtBeat(3));
    }

    [Fact]
    public void PositionSitsOnThePlayheadLine()
    {
        var viewport = Create();
        Assert.Equal(viewport.PlayheadY, viewport.YAtBeat(viewport.Position), 6);
    }

    [Fact]
    public void PlayheadLineIsAtTheBottomOfTheCanvas()
    {
        var viewport = Create();
        Assert.True(viewport.PlayheadY > viewport.ViewportHeight - 40);
        Assert.True(viewport.PlayheadY < viewport.ViewportHeight);
    }

    [Fact]
    public void BeatAtYIsInverseOfYAtBeat()
    {
        var viewport = Create();
        foreach (var beat in new[] { 0.0, 2.5, 4.0, 9.25 })
        {
            var y = viewport.YAtBeat(beat);
            Assert.Equal(beat, viewport.BeatAtY(y), 6);
        }
    }

    [Fact]
    public void PositionIsTheSingleSourceOfTruth()
    {
        var viewport = Create();
        var raised = 0;
        viewport.ViewChanged += (_, _) => raised++;

        viewport.SetPosition(6);
        Assert.Equal(6, viewport.Position, 6);
        Assert.Equal(1, raised);

        // 同样的值不会重复触发
        viewport.SetPosition(6);
        Assert.Equal(1, raised);

        // 缩放同样会广播
        viewport.ZoomAt(0, 1.2);
        Assert.Equal(2, raised);
    }

    [Fact]
    public void ScrollingNeverProducesNegativeBeats()
    {
        var viewport = Create();
        for (var i = 0; i < 200; i++)
        {
            viewport.ScrollByPixels(-1000);
        }

        Assert.True(viewport.Position >= 0);
        Assert.True(viewport.BottomBeat <= 0);   // 判定线以下的窄带允许为负，但不会被标注
        Assert.Equal(0, viewport.Position, 6);
    }

    [Fact]
    public void ScrollingDownMovesContentDownAndShowsLaterBeats()
    {
        var viewport = Create();
        var before = viewport.YAtBeat(4);
        viewport.ScrollByPixels(50);
        Assert.True(viewport.YAtBeat(4) > before);     // 同一拍在屏幕上向下移动
        Assert.True(viewport.Position > 4);          // 锚点后移，判定线处是更晚的拍
    }

    [Fact]
    public void ZoomKeepsAnchorNonNegative()
    {
        var viewport = Create();
        viewport.ScrollToBeat(0);
        viewport.ZoomAt(0, 0.5);
        Assert.True(viewport.Position >= 0);
    }
}

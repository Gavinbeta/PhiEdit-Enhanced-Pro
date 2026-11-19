using PhiEdit.Core.Layout;
using Xunit;

namespace PhiEdit.Core.Tests;

public class ViewportTests
{
    private static EditViewport Create(double height = 600, double inset = 26)
    {
        return new EditViewport
        {
            ViewportHeight = height,
            PlayheadInset = inset,
            PixelsPerBeat = 100,
            AnchorBeat = 4,
        };
    }

    [Fact]
    public void LaterBeatsAreDrawnHigher()
    {
        var viewport = Create();
        Assert.True(viewport.YAtBeat(5) < viewport.YAtBeat(4));
        Assert.True(viewport.YAtBeat(4) < viewport.YAtBeat(3));
    }

    [Fact]
    public void AnchorBeatSitsOnThePlayheadLine()
    {
        var viewport = Create();
        Assert.Equal(viewport.PlayheadY, viewport.YAtBeat(viewport.AnchorBeat), 6);
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
    public void ScrollingNeverProducesNegativeBeats()
    {
        var viewport = Create();
        for (var i = 0; i < 200; i++)
        {
            viewport.ScrollByPixels(-1000);
        }

        Assert.True(viewport.AnchorBeat >= 0);
        Assert.True(viewport.BottomBeat <= 0);   // 判定线以下的窄带允许为负，但不会被标注
        Assert.Equal(0, viewport.AnchorBeat, 6);
    }

    [Fact]
    public void ScrollingDownMovesContentDownAndShowsLaterBeats()
    {
        var viewport = Create();
        var before = viewport.YAtBeat(4);
        viewport.ScrollByPixels(50);
        Assert.True(viewport.YAtBeat(4) > before);     // 同一拍在屏幕上向下移动
        Assert.True(viewport.AnchorBeat > 4);          // 锚点后移，判定线处是更晚的拍
    }

    [Fact]
    public void ZoomKeepsAnchorNonNegative()
    {
        var viewport = Create();
        viewport.ScrollToBeat(0);
        viewport.ZoomAt(0, 0.5);
        Assert.True(viewport.AnchorBeat >= 0);
    }
}

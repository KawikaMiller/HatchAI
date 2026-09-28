using Avalonia;
using Xunit;

namespace HatchAI.Tests;

// Where the companion and its bubble go. The one rule that must never break
// is that a bubble does not overlap its companion — CB-104's flicker came from
// exactly that — so besides a case per edge there is a sweep that parks the
// companion everywhere on a small screen and checks every answer.
public class BuddyPlacementTests
{
    private static readonly PixelRect Work = new(0, 0, 1920, 1040);
    private static readonly PixelSize Companion = new(96, 114);
    private static readonly PixelSize BubbleSize = new(200, 60);

    [Fact]
    public void FirstAppearanceIsTheBottomRightCornerWithAMargin()
    {
        var at = BuddyPlacement.DefaultCompanion(Companion, Work);

        Assert.Equal(new PixelPoint(1920 - 96 - 24, 1040 - 114 - 24), at);
    }

    [Fact]
    public void FirstAppearanceOnASecondMonitorLeftOfThePrimary()
    {
        var work = new PixelRect(-1280, 40, 1280, 984);

        var at = BuddyPlacement.DefaultCompanion(Companion, work);

        Assert.Equal(new PixelPoint(-96 - 24, 40 + 984 - 114 - 24), at);
    }

    [Fact]
    public void FirstAppearanceOnAWorkAreaSmallerThanTheBuddyPinsTopLeft()
    {
        var work = new PixelRect(10, 20, 50, 50);

        Assert.Equal(new PixelPoint(10, 20), BuddyPlacement.DefaultCompanion(Companion, work));
    }

    [Theory]
    [InlineData(-5000, 300, 0, 300)]      // off the left, a monitor since unplugged
    [InlineData(5000, 300, 1920 - 96, 300)] // off the right
    [InlineData(500, -400, 500, 0)]       // above the top
    [InlineData(500, 9000, 500, 1040 - 114)] // below the bottom
    [InlineData(700, 500, 700, 500)]      // on screen: left exactly where it was
    public void ASavedPositionIsRescuedOntoTheWorkArea(int x, int y, int ex, int ey)
    {
        Assert.Equal(new PixelPoint(ex, ey), BuddyPlacement.ClampCompanion(new PixelPoint(x, y), Companion, Work));
    }

    [Fact]
    public void TheFootprintIsNeverSmallerThanWindowsWillAllow()
    {
        Assert.Equal(new PixelSize(136, 114), BuddyPlacement.Footprint(new PixelSize(96, 114)));
        Assert.Equal(new PixelSize(200, 39), BuddyPlacement.Footprint(new PixelSize(200, 20)));
        Assert.Equal(new PixelSize(300, 200), BuddyPlacement.Footprint(new PixelSize(300, 200)));
    }

    [Fact]
    public void InTheMiddleTheBubbleGoesAboveCentred()
    {
        var companion = new PixelRect(new PixelPoint(900, 500), Companion);

        var at = BuddyPlacement.Bubble(companion, BubbleSize, Work);

        Assert.Equal(new PixelPoint(900 + (96 - 200) / 2, 500 - BuddyPlacement.Gap - 60), at);
        Assert.Equal(BubbleSide.Above, BuddyPlacement.SideOf(at, BubbleSize, companion));
    }

    [Fact]
    public void AgainstTheTopEdgeItGoesBelow()
    {
        var companion = new PixelRect(new PixelPoint(900, 10), Companion);

        var at = BuddyPlacement.Bubble(companion, BubbleSize, Work);

        Assert.Equal(new PixelPoint(848, 10 + 114 + BuddyPlacement.Gap), at);
        Assert.Equal(BubbleSide.Below, BuddyPlacement.SideOf(at, BubbleSize, companion));
    }

    [Fact]
    public void AgainstTheLeftEdgeItSlidesRightButStaysAbove()
    {
        var companion = new PixelRect(new PixelPoint(0, 500), Companion);

        var at = BuddyPlacement.Bubble(companion, BubbleSize, Work);

        Assert.Equal(new PixelPoint(0, 500 - BuddyPlacement.Gap - 60), at);
    }

    [Fact]
    public void AgainstTheRightEdgeItSlidesLeftButStaysAbove()
    {
        var companion = new PixelRect(new PixelPoint(1920 - 96, 500), Companion);

        var at = BuddyPlacement.Bubble(companion, BubbleSize, Work);

        Assert.Equal(new PixelPoint(1920 - 200, 500 - BuddyPlacement.Gap - 60), at);
    }

    [Fact]
    public void AgainstTheBottomEdgeItStillGoesAbove()
    {
        var companion = new PixelRect(new PixelPoint(900, 1040 - 114), Companion);

        var at = BuddyPlacement.Bubble(companion, BubbleSize, Work);

        Assert.Equal(1040 - 114 - BuddyPlacement.Gap - 60, at.Y);
    }

    [Fact]
    public void AScreenTooShortForAboveOrBelowPutsItToTheRight()
    {
        var work = new PixelRect(0, 0, 800, 130);
        var companion = new PixelRect(new PixelPoint(100, 8), Companion);

        var at = BuddyPlacement.Bubble(companion, BubbleSize, work);

        Assert.Equal(new PixelPoint(100 + 96 + BuddyPlacement.Gap, 8 + (114 - 60) / 2), at);
        Assert.Equal(BubbleSide.Right, BuddyPlacement.SideOf(at, BubbleSize, companion));
    }

    [Fact]
    public void AndToTheLeftWhenTheRightIsTheScreenEdge()
    {
        var work = new PixelRect(0, 0, 800, 130);
        var companion = new PixelRect(new PixelPoint(800 - 96, 8), Companion);

        var at = BuddyPlacement.Bubble(companion, BubbleSize, work);

        Assert.Equal(new PixelPoint(800 - 96 - BuddyPlacement.Gap - 200, 8 + (114 - 60) / 2), at);
        Assert.Equal(BubbleSide.Left, BuddyPlacement.SideOf(at, BubbleSize, companion));
    }

    // Nowhere fits whole: the bubble goes wherever there is most room and
    // hangs off the screen rather than onto the buddy.
    [Theory]
    [InlineData(120, 400, 0, 0, "Below")]
    [InlineData(120, 400, 0, 286, "Above")]
    [InlineData(300, 180, 0, 30, "Right")]
    [InlineData(300, 180, 200, 30, "Left")]
    public void WhenNothingFitsItPicksTheRoomiestSideAndStillDoesNotOverlap(int w, int h, int x, int y, string expectedSide)
    {
        var work = new PixelRect(0, 0, w, h);
        var companion = new PixelRect(new PixelPoint(x, y), Companion);
        var wide = new PixelSize(400, 100);

        var at = BuddyPlacement.Bubble(companion, wide, work);

        Assert.Equal(Enum.Parse<BubbleSide>(expectedSide), BuddyPlacement.SideOf(at, wide, companion));
        Assert.False(new PixelRect(at, wide).Intersects(companion));
    }

    [Fact]
    public void ANeverOverlapSweepAcrossASmallScreen()
    {
        var work = new PixelRect(-640, 30, 1280, 690);
        var companion = BuddyPlacement.Footprint(Companion);
        var sizes = new[] { new PixelSize(136, 39), BubbleSize, new PixelSize(280, 420), new PixelSize(1400, 800) };

        foreach (var size in sizes)
        {
            for (var x = work.X - 50; x <= work.Right; x += 37)
            {
                for (var y = work.Y - 50; y <= work.Bottom; y += 29)
                {
                    var rect = new PixelRect(new PixelPoint(x, y), companion);
                    var at = BuddyPlacement.Bubble(rect, size, work);
                    Assert.False(new PixelRect(at, size).Intersects(rect), $"bubble {size} at {at} overlaps companion {rect}");
                }
            }
        }
    }
}

using Avalonia;
using Xunit;

namespace HatchAI.Tests
{
    // ChatPanelPlacement.ClampSavedPosition: the one rule HatchAI kept from
    // Claude Buddy's chat-panel placement, because BuddyPlacement restores the
    // buddy's saved position through it. These are Claude Buddy's own
    // ClampSavedPosition cases, verbatim; the Resolve cases went with Resolve.
    public class ChatPanelPlacementTests
    {
        private static readonly PixelSize DefaultSize = new(400, 300);
        // --- CB-111: ClampSavedPosition ------------------------------------

        // A saved position already inside the work area comes back unchanged
        // — the ordinary case, exercised on every restart of a pin that was
        // never left near an edge.
        [Fact]
        public void ClampSavedPosition_LeavesAnOnScreenPositionAlone()
        {
            var work = new PixelRect(0, 0, 1920, 1080);
            var saved = new PixelPoint(500, 400);

            Assert.Equal(saved, ChatPanelPlacement.ClampSavedPosition(saved, DefaultSize, work));
        }

        // Off each edge in turn, sized to a real panel rather than a square
        // orb footprint — the reason this exists separately from
        // SessionManager's own ClampIntoWork/ClampIntoWork tests: a 400x300
        // panel's far edge is 400 or 300 px from its saved top-left, not 56,
        // so the right/bottom limits this pulls back to are different numbers
        // than an orb restoring to the same work area would use.
        [Theory]
        [InlineData(-200, 400, 0, 400)]
        [InlineData(500, -200, 500, 0)]
        [InlineData(5000, 400, 1520, 400)]
        [InlineData(500, 5000, 500, 780)]
        public void ClampSavedPosition_PullsAPanelBackUntilAllOfItIsOnScreen(
            int x, int y, int expectedX, int expectedY)
        {
            var work = new PixelRect(0, 0, 1920, 1080);

            var clamped = ChatPanelPlacement.ClampSavedPosition(new PixelPoint(x, y), DefaultSize, work);

            Assert.Equal(new PixelPoint(expectedX, expectedY), clamped);
        }

        // A monitor to the left of the primary, or a menu bar eating into the
        // top of the work area — a non-origin work area, as a second monitor
        // reports.
        [Fact]
        public void ClampSavedPosition_RespectsAWorkAreaThatDoesNotStartAtTheOrigin()
        {
            var work = new PixelRect(-1920, 25, 1920, 1055);

            Assert.Equal(
                new PixelPoint(-1920, 25),
                ChatPanelPlacement.ClampSavedPosition(new PixelPoint(-3000, -100), DefaultSize, work));
        }

        // A panel bigger than the work area it is restored into pins to the
        // corner rather than crashing — Math.Clamp throws on inverted bounds
        // (`Right - size < X`), which is exactly what a work area smaller
        // than the panel produces without the Math.Max guard Clamp already
        // has.
        [Fact]
        public void ClampSavedPosition_SurvivesAWorkAreaSmallerThanThePanel()
        {
            var tiny = new PixelRect(100, 100, 20, 20);

            Assert.Equal(
                new PixelPoint(100, 100),
                ChatPanelPlacement.ClampSavedPosition(new PixelPoint(500, 500), DefaultSize, tiny));
        }
    }
}

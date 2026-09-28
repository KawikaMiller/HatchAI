using Avalonia;

namespace HatchAI
{
    // Where the companion and its bubble go. Owned by E3 (CB-195). Pure, in
    // the PixelRect/PixelPoint vocabulary ChatPanelPlacement already uses.
    //
    // The bubble must never overlap its anchor: an overlapping popup is what
    // produced CB-104's flicker. And never use a window's actual size as an
    // input — Windows enforces a minimum top-level size (136x39), so the size
    // you asked for and the size you got can differ.
    internal static class BuddyPlacement
    {
        // Daylight between the companion and the edge of the screen it first
        // appears against. Enough that it does not read as stuck to the
        // taskbar or the Dock, small enough that it stays out of the way.
        internal const int EdgeMargin = 24;

        // Daylight between the companion and its bubble or card.
        internal const int Gap = 4;

        // The floor Windows puts under any top-level window, SM_CXMINTRACK x
        // SM_CYMINTRACK on a stock Windows 11 desktop (see the comment on
        // OrbWindow.axaml's Root, where it was measured). Every window here
        // asks for a size and may be handed a bigger one, with the extra as
        // invisible, still-present window to the right and below of what was
        // drawn. A rect that is meant to say "where this window is" has to
        // include that, or a bubble placed to its right lands inside the
        // companion's invisible remainder — the overlap CB-104 was about, by
        // a route nobody can see.
        //
        // Applied on every platform, not only Windows: on macOS it costs a
        // few pixels of extra gap, and one rule for both is one rule to test.
        internal static readonly PixelSize WindowsMinimum = new(136, 39);

        // What a window asking for `requested` actually occupies, at worst.
        internal static PixelSize Footprint(PixelSize requested) => new(
            Math.Max(requested.Width, WindowsMinimum.Width),
            Math.Max(requested.Height, WindowsMinimum.Height));

        // Where a companion with no saved position first appears: the
        // bottom-right corner of the work area, which on both platforms is the
        // corner nearest the clock and furthest from where people read.
        internal static PixelPoint DefaultCompanion(PixelSize companion, PixelRect workArea) =>
            ClampCompanion(
                new PixelPoint(
                    workArea.Right - companion.Width - EdgeMargin,
                    workArea.Bottom - companion.Height - EdgeMargin),
                companion,
                workArea);

        // A saved position pulled back onto the current work area. The rescue
        // for a companion saved on a monitor that has since been unplugged:
        // ChatPanelPlacement's clamp, reused rather than restated, so the two
        // cannot drift into rescuing differently.
        internal static PixelPoint ClampCompanion(PixelPoint saved, PixelSize companion, PixelRect workArea) =>
            ChatPanelPlacement.ClampSavedPosition(saved, companion, workArea);

        // Top-left of the bubble beside `companion`, inside `workArea`, never
        // intersecting `companion`.
        //
        // Above first, because a speech bubble over a head is the shape
        // everybody already reads; then below, right and left. Above and
        // below are centred on the companion and slide sideways to stay on
        // screen; sliding cannot make them overlap, since they are separated
        // vertically. Right and left are centred vertically and slide up and
        // down for the same reason.
        //
        // When no side fits the work area whole — a companion dragged into a
        // corner of a tiny display — non-overlap wins over on-screen, the
        // same call OrbWindow.PlaceThoughtBubbleAboveAnchor makes: a bubble
        // partly off-screen is a cosmetic problem, a bubble on top of its
        // anchor is CB-104's flicker loop. The fallback is whichever side has
        // the most room, still clamped along its free axis.
        internal static PixelPoint Bubble(PixelRect companion, PixelSize bubble, PixelRect workArea)
        {
            var centreX = companion.X + (companion.Width - bubble.Width) / 2;
            var centreY = companion.Y + (companion.Height - bubble.Height) / 2;

            var above = new PixelPoint(SlideX(centreX, bubble, workArea), companion.Y - Gap - bubble.Height);
            var below = new PixelPoint(SlideX(centreX, bubble, workArea), companion.Bottom + Gap);
            var right = new PixelPoint(companion.Right + Gap, SlideY(centreY, bubble, workArea));
            var left = new PixelPoint(companion.X - Gap - bubble.Width, SlideY(centreY, bubble, workArea));

            foreach (var candidate in new[] { above, below, right, left })
            {
                if (Contains(workArea, new PixelRect(candidate, bubble))) return candidate;
            }

            var roomAbove = companion.Y - workArea.Y;
            var roomBelow = workArea.Bottom - companion.Bottom;
            var roomRight = workArea.Right - companion.Right;
            var roomLeft = companion.X - workArea.X;

            var best = Math.Max(Math.Max(roomAbove, roomBelow), Math.Max(roomRight, roomLeft));

            if (best == roomAbove) return above;
            if (best == roomBelow) return below;
            if (best == roomRight) return right;
            return left;
        }

        // Which side of the companion Bubble put the bubble on, so the bubble
        // can point its tail back at it. Derived from the answer rather than
        // returned alongside it, which keeps Bubble's contract a single point.
        internal static BubbleSide SideOf(PixelPoint bubbleTopLeft, PixelSize bubble, PixelRect companion)
        {
            if (bubbleTopLeft.Y + bubble.Height <= companion.Y) return BubbleSide.Above;
            if (bubbleTopLeft.Y >= companion.Bottom) return BubbleSide.Below;
            if (bubbleTopLeft.X >= companion.Right) return BubbleSide.Right;
            return BubbleSide.Left;
        }

        // Slide along one axis to stay inside the work area; a bubble wider
        // than the work area pins to its left edge, as ChatPanelPlacement's
        // clamp does, rather than running negative.
        private static int SlideX(int x, PixelSize bubble, PixelRect work) =>
            Math.Clamp(x, work.X, Math.Max(work.X, work.Right - bubble.Width));

        private static int SlideY(int y, PixelSize bubble, PixelRect work) =>
            Math.Clamp(y, work.Y, Math.Max(work.Y, work.Bottom - bubble.Height));

        private static bool Contains(PixelRect outer, PixelRect inner) =>
            inner.X >= outer.X && inner.Y >= outer.Y
            && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;
    }

    internal enum BubbleSide { Above, Below, Right, Left }
}

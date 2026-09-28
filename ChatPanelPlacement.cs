using Avalonia;

namespace HatchAI
{
    // The two clamping rules BuddyPlacement borrows, and nothing else.
    //
    // In Claude Buddy this class also decides where a chat panel opens beside
    // an orb, dodging panels already pinned on screen. HatchAI has no chat
    // panels, so that half was left behind. The class keeps its name so the
    // buddy's own files, which call ChatPanelPlacement.ClampSavedPosition,
    // port without an edit — a rename here would buy nothing but a diff
    // against the code the buddy was tested with.
    internal static class ChatPanelPlacement
    {
        // Math.Clamp into `work`, and when the window is bigger than the work
        // area itself, pinned to its top-left rather than centred or left to
        // run negative — the Math.Max guard.
        private static PixelRect Clamp(PixelRect candidate, PixelSize size, PixelRect work)
        {
            var x = Math.Clamp(candidate.X, work.X, Math.Max(work.X, work.Right - size.Width));
            var y = Math.Clamp(candidate.Y, work.Y, Math.Max(work.Y, work.Bottom - size.Height));
            return new PixelRect(new PixelPoint(x, y), size);
        }

        // A saved position, pulled back onto whatever work area it is being
        // restored into. Sized to the window rather than clamping only its
        // top-left corner, so a monitor that shrank since the position was
        // saved cannot leave the far edge hanging off it.
        internal static PixelPoint ClampSavedPosition(PixelPoint saved, PixelSize size, PixelRect work) =>
            Clamp(new PixelRect(saved, size), size, work).Position;
    }
}

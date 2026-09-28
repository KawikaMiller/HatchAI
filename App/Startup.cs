using System;
using Avalonia.Threading;

namespace HatchAI
{
    // The order the process starts in, as something other than the inside of
    // Main.
    //
    // Ported from Claude Buddy, trimmed to the four steps HatchAI has: there
    // is no relay or peer link to serve before the UI, and no macOS
    // screen-lock wait yet (a documented follow-up for a Login Item start on a
    // Mac). The order of what is left is exactly Claude Buddy's, for the
    // reasons it paid for (CB-28, CB-44, CB-178).
    internal static class Startup
    {
        // Claim Avalonia's UI-thread dispatcher for the thread that is calling.
        //
        // **Reading this property is the whole operation, and it is not a
        // no-op.** In Avalonia 12.1.1, `Dispatcher.UIThread` falls through to
        // `CurrentDispatcher` when nothing has made a dispatcher yet, and that
        // constructs `new Dispatcher(null)` *on the calling thread*, whose
        // constructor does `s_uiThread ??= this`. Whichever thread touches it
        // first owns the UI thread for the life of the process. Claude Buddy
        // measured a pool thread getting there first and platform init then
        // throwing "The calling thread cannot access this object because a
        // different thread owns it" (CB-28). Nothing HatchAI starts before the
        // UI posts from the pool today, so this is a guard rather than a fix —
        // kept because it costs a field read and the failure it prevents takes
        // the process down.
        internal static void ClaimUiThread() => _ = Dispatcher.UIThread;

        // Main's body, with the things it does passed in.
        //
        // Crash logging first, so a failure in anything below it is written
        // down (CB-44: two crashes in Claude Buddy left nothing behind). The
        // single-instance claim next, and before everything else: a duplicate
        // instance should do *nothing* — not claim the UI thread, not start
        // Avalonia — and return from Main normally. Claude Buddy used to make
        // this decision inside Avalonia's startup, where the loser's
        // Shutdown() tore the dispatcher down mid-startup and turned "another
        // one is running" into an uncaught exception and a SIGABRT (CB-178).
        //
        // For HatchAI the claim matters for a second reason: two instances
        // would both count the same transcripts into their own copy of the
        // buddy, and whichever saved last would win — tokens lost or counted
        // twice, silently.
        //
        // Passed as delegates because every one of them is unrunnable in a
        // test — a real named mutex, and a lifetime that owns the process
        // until it exits — while the order and the short-circuit are the part
        // a test can hold on to.
        internal static void Run(
            Action installCrashLog,
            Func<bool> claimSingleInstance,
            Action claimUiThread,
            Action startUi)
        {
            installCrashLog();
            if (!claimSingleInstance()) return;
            claimUiThread();
            startUi();
        }
    }
}

namespace HatchAI
{
    // The workflow moments a bubble can be about. All derivable from the
    // existing status states (idle, generating, waiting, ended) with no hook
    // change — the v1 decision on CB-195. A turn shorter than the 2 s scan is
    // simply never seen; that is accepted, not a bug.
    //
    // There is no Error moment: nothing in today's hook contract reports one,
    // and inventing it from a heuristic would put words in the buddy's mouth
    // about something that did not happen.
    internal enum BuddyMoment
    {
        SessionStarted,
        // generating for 8 s or more — once per generating stretch.
        Thinking,
        // generating/waiting -> idle; TurnSignal.Finished.
        Responded,
        // into waiting; TurnSignal.NeedsAttention (permission or input).
        NeedsAttention,
        // idle/waiting -> generating: the user answered or sent a prompt.
        UserResponded,
        // idle past the long-idle threshold — once per idle stretch.
        LongIdle,
        SessionEnded,
        // Not from a session: raised by the controller when BuddyProgress's
        // stage or stars go up, so evolution gets a bubble through the same
        // policy and rate limit as everything else.
        Evolved,
    }

    internal readonly record struct BuddyMomentEvent(string SessionId, BuddyMoment Moment);

    // Two consecutive scans and a clock in, moments out. Pure. Reuses
    // TurnSignals.Classify for Responded/NeedsAttention rather than restating
    // its rule, so the buddy and the chimes cannot disagree about what a turn
    // finishing is. Never returns Evolved.
    internal static class BuddyMoments
    {
        // Generating this long before the buddy says anything about it: short
        // enough to read as "still on it", long enough that a quick tool call
        // does not earn a bubble.
        internal static readonly TimeSpan ThinkingAfter = TimeSpan.FromSeconds(8);

        // An idle session this long has been left alone, not merely paused
        // between prompts.
        internal static readonly TimeSpan LongIdleAfter = TimeSpan.FromMinutes(15);

        private const string EndedState = "ended";
        private const string IdleState = "idle";
        private const string GeneratingState = "generating";
        private const string WaitingState = "waiting";

        // `previous` is null on the first scan, which yields nothing — the
        // same rule TurnSignals applies, so a relaunch does not announce every
        // session already on screen as having just started.
        //
        // `previousNow` is when `previous` was taken, and it is what makes
        // Thinking and LongIdle fire once instead of on every scan past the
        // threshold: they are "the threshold was crossed between the two
        // scans", which two snapshots alone cannot say because StateSince is a
        // start, not a scan time. Without it (null) neither fires, the safe
        // direction — a missing bubble, never a repeating one.
        internal static IReadOnlyList<BuddyMomentEvent> Classify(
            IReadOnlyList<SessionSnapshot>? previous,
            IReadOnlyList<SessionSnapshot> current,
            DateTimeOffset now,
            DateTimeOffset? previousNow = null)
        {
            if (previous is null) return Array.Empty<BuddyMomentEvent>();

            var before = new Dictionary<string, SessionSnapshot>(StringComparer.Ordinal);
            foreach (var s in previous) before[s.SessionId] = s;

            var events = new List<BuddyMomentEvent>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var cur in current)
            {
                seen.Add(cur.SessionId);

                if (!before.TryGetValue(cur.SessionId, out var prev))
                {
                    if (!IsEnded(cur)) events.Add(new(cur.SessionId, BuddyMoment.SessionStarted));
                    continue;
                }

                // The hook's own "about to be removed" marker: reported once,
                // and never as a finished turn (TurnSignals says the same).
                if (IsEnded(cur))
                {
                    if (!IsEnded(prev)) events.Add(new(cur.SessionId, BuddyMoment.SessionEnded));
                    continue;
                }

                var moment = Transition(prev, cur) ?? Crossing(prev, cur, previousNow, now);
                if (moment is { } m) events.Add(new(cur.SessionId, m));
            }

            foreach (var prev in previous)
            {
                if (!seen.Contains(prev.SessionId) && !IsEnded(prev))
                    events.Add(new(prev.SessionId, BuddyMoment.SessionEnded));
            }

            return events;
        }

        private static bool IsEnded(SessionSnapshot s) => Is(s, EndedState);

        private static bool Is(SessionSnapshot s, string state) =>
            string.Equals(s.State, state, StringComparison.Ordinal);

        private static BuddyMoment? Transition(SessionSnapshot prev, SessionSnapshot cur)
        {
            switch (TurnSignals.Classify(prev.State, cur.State))
            {
                case TurnSignal.Finished: return BuddyMoment.Responded;
                case TurnSignal.NeedsAttention: return BuddyMoment.NeedsAttention;
            }

            if (Is(cur, GeneratingState) && (Is(prev, IdleState) || Is(prev, WaitingState)))
                return BuddyMoment.UserResponded;

            return null;
        }

        // Same state in both scans, and the threshold fell between them.
        private static BuddyMoment? Crossing(
            SessionSnapshot prev, SessionSnapshot cur, DateTimeOffset? previousNow, DateTimeOffset now)
        {
            if (previousNow is not { } then) return null;
            if (!string.Equals(prev.State, cur.State, StringComparison.Ordinal)) return null;

            if (Is(cur, GeneratingState) && Crosses(cur.StateSince, then, now, ThinkingAfter))
                return BuddyMoment.Thinking;
            if (Is(cur, IdleState) && Crosses(cur.StateSince, then, now, LongIdleAfter))
                return BuddyMoment.LongIdle;
            return null;
        }

        // Whether a generated line may be asked for this moment (owner
        // decision on CB-202 after live use): only the four tied to the user's
        // current work, which is what a generated line is for. A welcome, a
        // goodbye, an idle stretch and an evolution have no fresh work to react
        // to, so they always take the table and spend nothing.
        //
        // Every arm is spelled out, and anything else throws, so a moment added
        // to the enum has to be classified here before it can speak — the
        // exhaustive test over the enum fails until it is.
        internal static bool MayUseAi(BuddyMoment moment) => moment switch
        {
            BuddyMoment.Responded => true,
            BuddyMoment.UserResponded => true,
            BuddyMoment.NeedsAttention => true,
            BuddyMoment.Thinking => true,
            BuddyMoment.SessionStarted => false,
            BuddyMoment.SessionEnded => false,
            BuddyMoment.LongIdle => false,
            BuddyMoment.Evolved => false,
            _ => throw new ArgumentOutOfRangeException(nameof(moment), moment, "unclassified moment"),
        };

        private static bool Crosses(DateTimeOffset since, DateTimeOffset then, DateTimeOffset now, TimeSpan threshold) =>
            now - since >= threshold && then - since < threshold;
    }
}

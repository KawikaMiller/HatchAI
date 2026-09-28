namespace HatchAI
{
    // Which session the one companion is currently reacting to. Pure.
    // `currentFocus` is the previous answer, passed back in so the rule can be
    // sticky — a companion that hops between two busy sessions on every scan
    // is reacting to nothing. Null when there are no sessions.
    //
    // A session that needs the user outranks everything, because that is the
    // one moment where the companion being about the wrong session costs
    // something. Otherwise the session that most recently changed state wins,
    // but only once the current focus has been quiet for StickyFor.
    internal static class BuddyFocus
    {
        internal static readonly TimeSpan StickyFor = TimeSpan.FromSeconds(10);

        private const string WaitingState = "waiting";

        internal static string? Choose(
            IReadOnlyList<SessionSnapshot> sessions,
            string? currentFocus,
            DateTimeOffset now)
        {
            if (sessions.Count == 0) return null;

            SessionSnapshot? best = null;
            SessionSnapshot? focus = null;
            foreach (var s in sessions)
            {
                if (best is null || Outranks(s, best)) best = s;
                if (currentFocus is not null && string.Equals(s.SessionId, currentFocus, StringComparison.Ordinal))
                    focus = s;
            }

            if (focus is null) return best!.SessionId;

            // A session that has started waiting since the focus did preempts
            // regardless of stickiness; so does one when the focus is not
            // waiting at all.
            if (IsWaiting(best!) && (!IsWaiting(focus) || best!.StateSince > focus.StateSince))
                return best!.SessionId;

            return now - focus.StateSince < StickyFor ? focus.SessionId : best!.SessionId;
        }

        private static bool IsWaiting(SessionSnapshot s) =>
            string.Equals(s.State, WaitingState, StringComparison.Ordinal);

        // Waiting before anything else, then most recent transition, then id
        // so a tie is decided the same way on every scan.
        private static bool Outranks(SessionSnapshot a, SessionSnapshot b)
        {
            var aw = IsWaiting(a);
            var bw = IsWaiting(b);
            if (aw != bw) return aw;
            if (a.StateSince != b.StateSince) return a.StateSince > b.StateSince;
            return string.CompareOrdinal(a.SessionId, b.SessionId) < 0;
        }
    }
}

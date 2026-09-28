namespace HatchAI
{
    // One session as the buddy sees it: a plain immutable copy, taken on the UI
    // thread at the end of a StatusReader scan and handed to BuddyController
    // (CB-195). The buddy never holds the StatusFile it came from, so nothing it
    // does can reach back into what the reader parsed.
    //
    // StateSince is when this state was first observed, carried forward by
    // whoever builds the snapshots (E4) while the state is unchanged. It is
    // what lets "thinking" (generating for 8 s or more) and "long idle" be
    // pure functions of two consecutive snapshots rather than timers.
    internal sealed record SessionSnapshot(
        string SessionId,
        // The hook's state string: idle, generating, waiting, ended.
        string State,
        DateTimeOffset StateSince,
        SessionSource Source,
        // What the session is called: its title, else its folder name, else its
        // id. (Claude Buddy's orbs prefer an agent-team name first; HatchAI has
        // no team information, and the buddy never reads this field anyway.)
        string Label,
        string Cwd,
        // Empty when there is none (cloud, OpenClaw, remote, old hooks).
        string TranscriptPath,
        // Non-empty on an agent-team member: its lead's session id. Always empty
        // in HatchAI, which cannot see teams; the buddy never reads it.
        string Lead);
}

namespace HatchAI
{
    // Builds the snapshots, and is the one place that remembers when each
    // session's state was first seen. StateSince is "first observed", not
    // "changed at": a status file carries no timestamp for the transition, so
    // a session already generating when the app launched reads as generating
    // since launch. That understates a long turn by however long the app had
    // been off, which for the buddy only delays a Thinking bubble, and is the
    // honest answer — nothing here can know better.
    internal sealed class SessionSnapshotTracker
    {
        private readonly Dictionary<string, (string State, DateTimeOffset Since)> _seen =
            new(StringComparer.Ordinal);

        internal IReadOnlyList<SessionSnapshot> Build(
            IEnumerable<(string Id, StatusFile Status)> sessions, DateTimeOffset now)
        {
            var result = new List<SessionSnapshot>();
            var live = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (id, status) in sessions)
            {
                live.Add(id);

                var since = now;
                if (_seen.TryGetValue(id, out var known)
                    && string.Equals(known.State, status.State, StringComparison.Ordinal))
                {
                    since = known.Since;
                }
                _seen[id] = (status.State, since);

                result.Add(new SessionSnapshot(
                    id,
                    status.State,
                    since,
                    status.Source,
                    DisplayName(id, status),
                    status.Cwd,
                    status.TranscriptPath,
                    Lead: ""));
            }

            // A session that came back after a gap starts a fresh stretch
            // rather than inheriting one from before it left.
            foreach (var id in _seen.Keys.Where(k => !live.Contains(k)).ToList()) _seen.Remove(id);

            return result;
        }

        // Claude Buddy's tray DisplayName without the agent-team arm: the title,
        // else the last segment of the cwd (either separator, since a WSL
        // session's cwd is a POSIX path), else the session id.
        internal static string DisplayName(string id, StatusFile status)
        {
            if (!string.IsNullOrWhiteSpace(status.Title)) return status.Title.Trim();
            var leaf = status.Cwd.Split('/', '\\').LastOrDefault(p => p.Length > 0);
            return string.IsNullOrWhiteSpace(leaf) ? id : leaf;
        }
    }
}

namespace HatchAI
{
    // Which Codex home folders the buddy's ledger walks for rollouts.
    //
    // Copied verbatim from Claude Buddy's CodexUsage.cs, where it sits among
    // the rate-limit parsing for Codex account orbs. HatchAI draws no account
    // orbs, so only this one method came across; the class keeps its name so
    // BuddyLedgerScanner's call to it ports unchanged.
    //
    // ~/.codex is always first and always present; each extra is taken as-is
    // when rooted and under the home folder otherwise, and an extra that names
    // ~/.codex again is not listed twice.
    internal static class CodexUsageAccounts
    {
        internal static List<string> Homes(string home, IReadOnlyList<string> extras)
        {
            var codex = Path.Combine(home, ".codex");
            var dirs = new List<string> { codex };
            foreach (var extra in extras)
            {
                if (string.IsNullOrWhiteSpace(extra)) continue;
                var path = extra.StartsWith(Path.DirectorySeparatorChar)
                           || extra.StartsWith(Path.AltDirectorySeparatorChar)
                    ? extra
                    : Path.Combine(home, extra);
                if (!string.Equals(path, codex, StringComparison.Ordinal))
                    dirs.Add(path);
            }

            return dirs;
        }
    }
}

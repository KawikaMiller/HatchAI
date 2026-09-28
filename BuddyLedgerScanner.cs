namespace HatchAI
{
    // What one scan earned, as a delta against the state it was handed.
    //
    // A delta rather than a new BuddyState because the scan runs off the UI
    // thread and the state can move underneath it — a rebirth, a rename. The
    // caller applies this to whatever the state is *now*, on the UI thread:
    //
    //     state = BuddyProgress.Credit(result.ApplyLedger(state), result.OutputTokens);
    //
    // and then saves, so the tokens and the cursors that earned them land in
    // the same write.
    internal sealed record LedgerScanResult(
        // Only the files this scan read or reset; everything else is untouched.
        IReadOnlyDictionary<string, LedgerCursor> Cursors,
        // Files that are gone from disk, whose cursors can be dropped.
        IReadOnlyList<string> Removed,
        long OutputTokens,
        IReadOnlyList<string> CreditedMessageIds)
    {
        internal static readonly LedgerScanResult Empty = new(
            new Dictionary<string, LedgerCursor>(), Array.Empty<string>(), 0, Array.Empty<string>());

        // The ledger half of applying a scan: cursors merged by path, removed
        // ones dropped, credited ids folded into the bounded recent set. The
        // token half is BuddyProgress.Credit's, which owns how Tokens and
        // LifetimeTokens move together.
        internal BuddyState ApplyLedger(BuddyState state)
        {
            var cursors = new Dictionary<string, LedgerCursor>(state.Cursors, StringComparer.Ordinal);
            foreach (var path in Removed) cursors.Remove(path);
            foreach (var (path, cursor) in Cursors) cursors[path] = cursor;

            return state with
            {
                Cursors = cursors,
                RecentMessageIds = BuddyLedger.FoldRecent(state.RecentMessageIds, CreditedMessageIds)
            };
        }
    }

    // Finds the transcripts worth counting and feeds their new bytes to
    // BuddyLedger. Owned by E2 (CB-195).
    //
    // Everything here is file I/O and none of it touches the UI thread: the
    // controller calls Scan from a background task and applies the result
    // itself. One instance is used by one scan at a time — it remembers the
    // size and mtime of every file it has looked at, which is what makes the
    // 60-second walk cheap, and that memory is not thread-safe.
    //
    // Two entry points, matching the plan's two cadences: ScanAll walks every
    // account's projects tree and Codex's sessions tree (every 60 s), and
    // ScanPaths reads only the transcripts of the sessions on screen right
    // now (every 10 s), so a live session's buddy moves without waiting on
    // the walk.
    internal sealed class BuddyLedgerScanner
    {
        // How much of one file is read into memory per call to the ledger. A
        // first scan of a long transcript is read in several of these rather
        // than all at once.
        internal const int DefaultWindowBytes = 8 * 1024 * 1024;

        private readonly string _home;
        private readonly IReadOnlyList<string>? _claudeRoots;
        private readonly IReadOnlyList<string>? _codexHomes;
        private readonly int _window;

        // Length and last-write time per file at its last read. A file whose
        // pair has not moved is not opened.
        //
        // The offset the read stopped at is part of the key: if the caller
        // never applied a result, the cursor it hands back next time is behind
        // where this remembers stopping, and the file is read again rather
        // than skipped as unchanged.
        private readonly Dictionary<string, (long Length, DateTime Written, long Offset)> _seen =
            new(StringComparer.Ordinal);

        // `home`, `claudeRoots` and `codexHomes` are the test seam — the same
        // one ClaudeConfigRoots.All(home) takes — so a suite can point the
        // walk at a temp tree. Left null, the roots are read from settings on
        // every ScanAll, so an account added in the settings window is picked
        // up without a restart. `windowBytes` exists so a test can drive the
        // multi-window and oversize-line paths without writing megabytes.
        internal BuddyLedgerScanner(
            string? home = null,
            IReadOnlyList<string>? claudeRoots = null,
            IReadOnlyList<string>? codexHomes = null,
            int windowBytes = DefaultWindowBytes)
        {
            _home = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            _claudeRoots = claudeRoots;
            _codexHomes = codexHomes;
            _window = Math.Max(windowBytes, 1);
        }

        // ---- discovery ------------------------------------------------------

        // Every transcript this machine has, with its dialect:
        // <root>/projects/*/*.jsonl and <root>/projects/*/*/subagents/*.jsonl
        // for each Claude Code account, and <home>/sessions/**/rollout-*.jsonl
        // for each Codex home.
        internal IEnumerable<(string Path, LedgerFormat Format)> Discover()
        {
            var claudeRoots = _claudeRoots ?? ClaudeConfigRoots.All(_home);
            foreach (var root in claudeRoots)
            {
                var projects = Path.Combine(root, "projects");
                foreach (var project in Directories(projects))
                {
                    // The app's own `claude -p` calls — the buddy's generated
                    // bubbles (CB-202) and the spoken summary — run from
                    // BubbleVoice.WorkDir, so anything they ever write lands in
                    // a folder named after it. Those tokens are the buddy
                    // talking to itself, and counting them would let it evolve
                    // on its own voice. Both calls also pass
                    // --no-session-persistence, so normally there is nothing
                    // here to skip; this is the layer that holds if a CLI
                    // release stops honouring the flag.
                    //
                    // Keyed on the real folder's name, not on an encoding of
                    // WorkDir: see IsOwnWorkDir for why no encoding is trusted.
                    //
                    // Not refunded: spoken summaries ran from the plain temp
                    // folder before this fix, and the transcripts they left
                    // there were counted then and stay counted. Their cursors
                    // sit in a folder this does not skip, and nothing new is
                    // written there any more.
                    if (BubbleVoice.IsOwnWorkDir(Path.GetFileName(project))) continue;

                    foreach (var file in Files(project, "*.jsonl", recurse: false))
                        yield return (file, LedgerFormat.ClaudeCode);

                    // <project>/<session>/subagents/agent-*.jsonl — a
                    // subagent's output is written to its own file and would
                    // otherwise be invisible.
                    foreach (var session in Directories(project))
                    {
                        foreach (var file in Files(Path.Combine(session, "subagents"), "*.jsonl", recurse: false))
                            yield return (file, LedgerFormat.ClaudeCode);
                    }
                }
            }

            var codexHomes = _codexHomes ?? CodexUsageAccounts.Homes(_home, HatchAISettings.CodexHomes);
            foreach (var codexHome in codexHomes)
            {
                foreach (var file in Files(Path.Combine(codexHome, "sessions"), "rollout-*.jsonl", recurse: true))
                    yield return (file, LedgerFormat.Codex);
            }
        }

        // The dialect of a transcript named by a live session, or null for
        // one the ledger does not count (Grok's updates.jsonl, anything that
        // is not a .jsonl at all).
        internal static LedgerFormat? FormatOf(string path)
        {
            if (!path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return null;

            var name = Path.GetFileName(path);
            if (name.StartsWith("rollout-", StringComparison.OrdinalIgnoreCase)) return LedgerFormat.Codex;
            if (name.Equals("updates.jsonl", StringComparison.OrdinalIgnoreCase)) return null;
            return LedgerFormat.ClaudeCode;
        }

        // ---- scanning -------------------------------------------------------

        internal LedgerScanResult ScanAll(BuddyState state)
        {
            var found = Discover().ToList();

            // A cursor whose file has gone — Claude Code prunes transcripts
            // after its cleanup period — is dropped, or settings.json would
            // carry every transcript this machine ever had. Only when the
            // walk did not find it *and* the file really is not there, so a
            // directory that was briefly unreadable costs a skipped pass, not
            // a cursor: losing one would re-read that file from the start
            // with only countingSince and the recent ids to stop a recount.
            var discovered = new HashSet<string>(found.Select(f => f.Path), StringComparer.Ordinal);
            var removed = state.Cursors.Keys
                .Where(path => !discovered.Contains(path) && !File.Exists(path))
                .ToList();

            return Scan(state, found, removed);
        }

        internal LedgerScanResult ScanPaths(BuddyState state, IEnumerable<string?> transcriptPaths)
        {
            var files = new List<(string, LedgerFormat)>();
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in transcriptPaths)
            {
                if (string.IsNullOrWhiteSpace(path) || FormatOf(path) is not { } format) continue;
                if (unique.Add(path)) files.Add((path, format));
            }

            return Scan(state, files, Array.Empty<string>());
        }

        private LedgerScanResult Scan(
            BuddyState state, IReadOnlyList<(string Path, LedgerFormat Format)> files, IReadOnlyList<string> removed)
        {
            // One set across every file in the pass, so the same response in
            // a subagent's file and its parent's is credited once even when
            // both are read in the same scan.
            var recent = new HashSet<string>(state.RecentMessageIds, StringComparer.Ordinal);
            var cursors = new Dictionary<string, LedgerCursor>(StringComparer.Ordinal);
            var credited = new List<string>();
            var pruned = new List<string>();
            long earned = 0;

            foreach (var (path, format) in files)
            {
                var stored = state.Cursors.TryGetValue(path, out var known);
                var start = stored ? known : LedgerCursor.Start;
                var read = ReadFile(path, format, start, state.CountingSince, recent, out var beforeHatch);

                // A cursor stored for a file from before the hatch — the
                // build before this one wrote one for every such file — is
                // dropped: it carries nothing the floor does not.
                if (beforeHatch && stored) pruned.Add(path);
                if (read is null) continue;

                if (read.Cursor != start) cursors[path] = read.Cursor;
                earned = BuddyLedger.Add(earned, read.OutputTokens);
                foreach (var id in read.CreditedMessageIds)
                {
                    recent.Add(id);
                    credited.Add(id);
                }
            }

            foreach (var path in removed) _seen.Remove(path);

            return new LedgerScanResult(
                cursors, pruned.Count == 0 ? removed : removed.Concat(pruned).ToList(), earned, credited);
        }

        // Everything new in one file, or null when the file was unchanged,
        // could not be read this pass, or was written entirely before the
        // hatch (`beforeHatch`, so the caller can drop a cursor it no longer
        // needs).
        private LedgerRead? ReadFile(
            string path, LedgerFormat format, LedgerCursor cursor, DateTimeOffset since, HashSet<string> recent,
            out bool beforeHatch)
        {
            beforeHatch = false;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return null;

                // A file written entirely before the hatch cannot hold a row
                // worth counting, so it is skipped without reading a byte —
                // and without a cursor (QA on CB-195). The build before this
                // one stored "offset = length" for every such file, which on
                // a machine with years of transcripts put hundreds of entries
                // of ~350 bytes into settings.json and rewrote them all on
                // every save. The cursor bought nothing: if the file is ever
                // resumed, it is read from the top and every row in it from
                // before the hatch is stopped by the countingSince floor, as
                // a truncated file already is. (A row with no timestamp would
                // slip that floor; none was measured — see BuddyLedger.) The
                // stat above is all it costs per walk.
                //
                // Claude Code only: a Codex rollout's credit is a delta off
                // its running total, and the stored mark is what a resumed
                // rollout's next delta is taken from. Re-deriving it from the
                // top relies on every token_count row carrying a timestamp —
                // the envelope says so, the token_count shape is ASSUMED
                // (docs/buddy-design.md) — so Codex keeps its cursors until
                // a real rollout has been measured.
                if (format == LedgerFormat.ClaudeCode && info.LastWriteTimeUtc < since.UtcDateTime)
                {
                    beforeHatch = true;
                    return null;
                }

                if (_seen.TryGetValue(path, out var last)
                    && last == (info.Length, info.LastWriteTimeUtc, cursor.Offset))
                    return null;

                // Shorter than where we stopped: rewritten or truncated. Start
                // over from the top. The timestamp floor in the cursor (Claude
                // Code) or the high-water total (Codex) is what stops the rows
                // already counted from being counted again.
                if (info.Length < cursor.Offset) cursor = cursor with { Offset = 0 };

                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);

                long earned = 0;
                var credited = new List<string>();
                var buffer = new byte[(int)Math.Min(_window, Math.Max(0, stream.Length - cursor.Offset))];
                while (cursor.Offset < stream.Length && buffer.Length > 0)
                {
                    stream.Position = cursor.Offset;
                    var count = Fill(stream, buffer);
                    if (count == 0) break;

                    var read = BuddyLedger.Read(format, cursor, buffer.AsMemory(0, count), since, recent);
                    earned = BuddyLedger.Add(earned, read.OutputTokens);
                    credited.AddRange(read.CreditedMessageIds);
                    foreach (var id in read.CreditedMessageIds) recent.Add(id);

                    if (read.Cursor.Offset > cursor.Offset)
                    {
                        cursor = read.Cursor;
                        continue;
                    }

                    // A full window with no newline in it: one line longer
                    // than the window. Skip past it if it has ended; if it has
                    // not, it is still being written and the next pass tries
                    // again.
                    if (count < buffer.Length) break;
                    var next = NextLineStart(stream, cursor.Offset + count);
                    if (next is null) break;
                    cursor = cursor with { Offset = next.Value };
                }

                _seen[path] = (info.Length, info.LastWriteTimeUtc, cursor.Offset);
                return new LedgerRead(cursor, earned, credited);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Deleted between the walk and the open, locked, or in a
                // directory this user cannot read. Nothing is lost by skipping
                // it: the cursor did not move, so the next pass reads the same
                // bytes.
                //
                // ArgumentException is a path the OS cannot name at all — a
                // NUL in a live session's transcript path makes FileInfo
                // throw it. It used to escape this catch and abort the whole
                // round, every other file with it (QA on CB-195); such a path
                // never becomes readable, so it simply costs itself.
                return null;
            }
        }

        private static int Fill(Stream stream, byte[] buffer)
        {
            var total = 0;
            while (total < buffer.Length)
            {
                var n = stream.Read(buffer, total, buffer.Length - total);
                if (n == 0) break;
                total += n;
            }

            return total;
        }

        // The offset just past the next '\n' at or after `from`, or null if
        // the file ends first.
        private static long? NextLineStart(Stream stream, long from)
        {
            stream.Position = from;
            var buffer = new byte[64 * 1024];
            var at = from;
            int n;
            while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                var newline = Array.IndexOf(buffer, (byte)'\n', 0, n);
                if (newline >= 0) return at + newline + 1;
                at += n;
            }

            return null;
        }

        // ---- enumeration ----------------------------------------------------

        // IgnoreInaccessible so one unreadable directory costs its own files
        // and not the whole walk. A missing directory is simply empty: most
        // accounts have no Codex home and a fresh one has no projects yet.
        private static readonly EnumerationOptions Flat = new()
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false
        };

        private static readonly EnumerationOptions Deep = new()
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true
        };

        private static IEnumerable<string> Directories(string dir) =>
            Guard(dir, () => Directory.EnumerateDirectories(dir, "*", Flat));

        private static IEnumerable<string> Files(string dir, string pattern, bool recurse) =>
            Guard(dir, () => Directory.EnumerateFiles(dir, pattern, recurse ? Deep : Flat));

        // Materialised inside the try, because enumeration is lazy and an
        // exception thrown while a caller iterates would escape it.
        private static IEnumerable<string> Guard(string dir, Func<IEnumerable<string>> enumerate)
        {
            if (!Directory.Exists(dir)) return Array.Empty<string>();
            try
            {
                return enumerate().ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not covered: a directory that existed a moment ago vanished
                // or refused to be listed mid-walk, despite IgnoreInaccessible
                // — a race no test stages deterministically. Named in the PR.
                return Array.Empty<string>();
            }
        }
    }
}

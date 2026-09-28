using System.Globalization;
using System.Text;
using System.Text.Json;

namespace HatchAI
{
    // Which transcript dialect a file is. Grok is deliberately absent: it only
    // documents a context gauge that shrinks on compaction, so it counts 0 in
    // v1 and its files are never handed to the ledger.
    internal enum LedgerFormat { ClaudeCode, Codex }

    // What one read of one file earned, and where to resume.
    //
    // CreditedMessageIds is every id this read credited anything to, in
    // order, for the caller to fold into BuddyState.RecentMessageIds (and trim
    // to its bound). Always empty for Codex, which has no per-response ids.
    internal sealed record LedgerRead(
        LedgerCursor Cursor,
        long OutputTokens,
        IReadOnlyList<string> CreditedMessageIds);

    // The counting rule, pure: bytes in, tokens out. Owned by E2 (CB-195).
    // BuddyLedgerScanner does the discovery and file I/O and hands this the
    // bytes from cursor.Offset to end-of-file.
    //
    // Measured against every Claude Code transcript on the Windows dev machine
    // (39 files, 10,382 assistant rows, read-only) before the rule was written:
    // 243 rows carried a higher output_tokens than an earlier row of the same
    // message.id, none of those ids came back after a different id had
    // intervened, no two assistant rows in a file shared a timestamp, none ran
    // backwards, the five isApiErrorMessage rows all said 0 output, and the
    // longest assistant row was 65,720 bytes.
    internal static class BuddyLedger
    {
        // Longer lines are consumed and skipped unparsed. Sixty-odd times the
        // longest assistant row measured, so a real row never meets it; what it
        // is for is a user row carrying a pasted file or image, which runs to
        // hundreds of kilobytes, can in principle run to anything, and is
        // never going to be counted anyway.
        internal const int MaxLineBytes = 4 * 1024 * 1024;

        // How many credited ids BuddyState keeps. Two ids in 5,739 were seen
        // in two files (a subagent's file and its parent's), written minutes
        // apart, so the window only has to reach back a few minutes of output
        // — two thousand responses is days of it.
        internal const int RecentIdLimit = 2000;

        // Cheap byte prefilters, so the several-hundred-kilobyte user and
        // attachment rows are skipped without being parsed. A row that passes
        // is still parsed and checked properly; these only ever let too much
        // through, never too little, because both CLIs write the string
        // verbatim inside the row they name.
        private static readonly byte[] AssistantMarker = Encoding.UTF8.GetBytes("\"assistant\"");
        private static readonly byte[] TokenCountMarker = Encoding.UTF8.GetBytes("\"token_count\"");

        // `chunk` starts at cursor.Offset. Consumes only up to the last '\n';
        // the returned cursor's Offset is cursor.Offset plus the bytes
        // consumed. Rows timestamped at or before `countingSince` advance the
        // cursor but credit nothing, and so do Claude Code rows at or before
        // the cursor's own LastTimestamp — that floor is what keeps a file
        // that shrank and was re-read from offset 0 from being counted twice.
        // A message id already in `recentIds` credits nothing (the same
        // response seen via another file).
        internal static LedgerRead Read(
            LedgerFormat format,
            LedgerCursor cursor,
            ReadOnlyMemory<byte> chunk,
            DateTimeOffset countingSince,
            IReadOnlyCollection<string> recentIds)
        {
            var span = chunk.Span;
            var end = span.LastIndexOf((byte)'\n');

            // No complete line yet: nothing consumed, nothing learned.
            if (end < 0) return new LedgerRead(cursor, 0, Array.Empty<string>());

            var state = new Walk(cursor, countingSince, recentIds);
            var start = 0;
            while (start <= end)
            {
                var length = span[start..].IndexOf((byte)'\n');
                var line = chunk.Slice(start, length);
                start += length + 1;

                if (line.Length == 0 || line.Length > MaxLineBytes) continue;

                if (format == LedgerFormat.ClaudeCode) state.ClaudeRow(line);
                else state.CodexRow(line);
            }

            return new LedgerRead(
                state.Cursor with { Offset = cursor.Offset + end + 1 },
                state.Earned,
                state.Credited);
        }

        // a + b, pinned at the ends of the range instead of wrapping. Every
        // token sum in the buddy goes through this (QA on CB-195): unchecked,
        // two responses of long.MaxValue added to -2, the controller read that
        // as "nothing earned" and dropped it while the cursors that read those
        // rows still advanced, so the credit was gone for good. No real
        // transcript gets near it; a hand-edited or corrupt one can, and
        // saturating costs nothing on the numbers that do occur.
        internal static long Add(long a, long b)
        {
            var sum = unchecked(a + b);

            // Overflow happened iff both operands share a sign the sum lacks.
            if (((a ^ sum) & (b ^ sum)) < 0) return a < 0 ? long.MinValue : long.MaxValue;
            return sum;
        }

        // The recent-id set after a read: newest last, duplicates dropped,
        // trimmed from the front to `limit`. Pure, so the scanner and the
        // caller that applies its result fold ids the same way.
        internal static IReadOnlyList<string> FoldRecent(
            IReadOnlyList<string> recent, IEnumerable<string> credited, int limit = RecentIdLimit)
        {
            var list = new List<string>(recent);
            var seen = new HashSet<string>(recent, StringComparer.Ordinal);
            foreach (var id in credited)
            {
                if (seen.Add(id)) list.Add(id);
            }

            return list.Count > limit ? list.GetRange(list.Count - limit, limit) : list;
        }

        // One pass over one chunk. A class rather than locals in Read so the
        // two dialects can share the cursor they both advance.
        private sealed class Walk
        {
            private readonly DateTimeOffset _since;
            private readonly IReadOnlyCollection<string> _recent;
            private readonly HashSet<string> _creditedSet = new(StringComparer.Ordinal);

            internal Walk(LedgerCursor cursor, DateTimeOffset since, IReadOnlyCollection<string> recent)
            {
                Cursor = cursor;
                _since = since;
                _recent = recent;
            }

            internal LedgerCursor Cursor { get; private set; }
            internal long Earned { get; private set; }
            internal List<string> Credited { get; } = new();

            internal void ClaudeRow(ReadOnlyMemory<byte> line)
            {
                if (line.Span.IndexOf(AssistantMarker) < 0) return;

                string? id;
                long output;
                DateTimeOffset? at;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var row = doc.RootElement;
                    if (row.ValueKind != JsonValueKind.Object) return;
                    if (Str(row, "type") != "assistant") return;

                    // Claude Code's own stand-in for a failed request. Every
                    // one measured said 0 output under a "<synthetic>" model,
                    // so skipping it changes no total today; it is skipped
                    // anyway so a future one that carries a number is not
                    // mistaken for work the model did.
                    if (row.TryGetProperty("isApiErrorMessage", out var error)
                        && error.ValueKind == JsonValueKind.True) return;

                    if (!row.TryGetProperty("message", out var message)
                        || message.ValueKind != JsonValueKind.Object) return;

                    id = Str(message, "id");
                    output = message.TryGetProperty("usage", out var usage)
                             && usage.ValueKind == JsonValueKind.Object
                        ? Long(usage, "output_tokens")
                        : 0;
                    at = Time(row);
                }
                catch (JsonException)
                {
                    // A torn or hand-mangled line. Skipped, not fatal: the
                    // rest of the file is still worth reading.
                    return;
                }

                // An id is what the whole dedup rests on; a row without one
                // cannot be told from its own repeat. None measured.
                if (id is null) return;

                if (at is { } stamp)
                {
                    if (stamp <= _since) return;
                    if (Cursor.LastTimestamp is { } last && stamp <= last) return;
                    Cursor = Cursor with { LastTimestamp = stamp };
                }

                if (id == Cursor.OpenMessageId)
                {
                    // The same response, written again with a higher count:
                    // credit only the rise. Never the sum — that is the rule
                    // the whole ledger exists to keep.
                    var rise = output - Cursor.OpenMessageOutput;
                    if (rise <= 0) return;
                    Earned = Add(Earned, rise);
                    Cursor = Cursor with { OpenMessageOutput = output };
                    Note(id);
                    return;
                }

                // Already credited through another file, or earlier in this
                // one after something else intervened. Not opened either, so
                // its later rows in this file are skipped the same way rather
                // than having their rises credited a second time.
                if (_creditedSet.Contains(id) || _recent.Contains(id)) return;

                Earned = Add(Earned, output);
                Cursor = Cursor with { OpenMessageId = id, OpenMessageOutput = output };
                Note(id);
            }

            // Codex rollouts. ASSUMED, not measured: Codex is not installed on
            // the dev machine, and the only part of this shape the repo has
            // seen for real is the envelope — {"timestamp", "type":"event_msg",
            // "payload":{"type":"token_count", ...}}, which CodexUsagePoller
            // already reads rate_limits out of. That payload.info.
            // total_token_usage.output_tokens is a running total for the
            // rollout is from memory of Codex's protocol; capture a real row
            // with non-null info before relying on the number.
            //
            // The running total is why this is a delta and not a sum: each
            // token_count restates everything so far, so the credit is how far
            // it moved past the last one seen. A total that goes *down* credits
            // nothing and does not lower the mark — rebasing would recount
            // everything between the dip and the old high the next time the
            // total climbs, and undercounting a reset is the cheaper mistake.
            internal void CodexRow(ReadOnlyMemory<byte> line)
            {
                if (line.Span.IndexOf(TokenCountMarker) < 0) return;

                long total;
                DateTimeOffset? at;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var row = doc.RootElement;
                    if (row.ValueKind != JsonValueKind.Object) return;
                    if (!row.TryGetProperty("payload", out var payload)
                        || payload.ValueKind != JsonValueKind.Object) return;
                    if (Str(payload, "type") != "token_count") return;

                    // A token_count with "info": null is what Codex writes
                    // before a turn has used anything (rate limits only). It
                    // says nothing about the total, so it moves nothing.
                    if (!payload.TryGetProperty("info", out var info)
                        || info.ValueKind != JsonValueKind.Object) return;
                    if (!info.TryGetProperty("total_token_usage", out var usage)
                        || usage.ValueKind != JsonValueKind.Object) return;

                    total = Long(usage, "output_tokens");
                    at = Time(row);
                }
                catch (JsonException)
                {
                    return;
                }

                if (total <= Cursor.LastCumulative) return;

                // Before the hatch: the mark moves so the delta after it is
                // only what was earned after it, but nothing is credited.
                if (at is { } stamp && stamp <= _since)
                {
                    Cursor = Cursor with { LastCumulative = total };
                    return;
                }

                // Both non-negative and total the larger, so the difference
                // cannot overflow; the running sum across rows can, in
                // principle, and saturates like the Claude Code path's.
                Earned = Add(Earned, total - Cursor.LastCumulative);
                Cursor = Cursor with { LastCumulative = total };
                if (at is { } seen && (Cursor.LastTimestamp is not { } prior || seen > prior))
                    Cursor = Cursor with { LastTimestamp = seen };
            }

            private void Note(string id)
            {
                if (_creditedSet.Add(id)) Credited.Add(id);
            }
        }

        private static string? Str(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        // A non-negative integer, or 0. A negative or fractional count is not
        // a number either CLI writes; treating it as nothing is safer than
        // letting it subtract from someone's buddy.
        private static long Long(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number)
            && number > 0
                ? number
                : 0;

        private static DateTimeOffset? Time(JsonElement row) =>
            Str(row, "timestamp") is { } text
            && DateTimeOffset.TryParse(
                text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                ? at
                : null;
    }
}

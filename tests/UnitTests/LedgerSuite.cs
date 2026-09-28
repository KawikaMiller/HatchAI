using System.Text;
using HatchAI;

namespace HatchAI.Tests
{
    // BuddyLedger's counting rule (CB-195), against rows shaped like the ones
    // Claude Code actually writes.
    //
    // The ledger reads the same files ChatTranscript does and fails the same
    // quiet way: a wrong rule never throws, it just hands the buddy a number.
    // Count every row of a response instead of its highest and a buddy
    // evolves on a quarter of the work; skip a rise that straddles two reads
    // and it evolves late. Neither shows up anywhere but in the number.
    //
    // The fixtures are real row shapes with every field the ledger does not
    // read removed and the text replaced. What was measured, read-only, over
    // the 39 transcripts on the Windows dev machine before writing them:
    //
    //   - An assistant row carries message.id, message.usage.output_tokens,
    //     and a top-level timestamp; usage also carries input, cache and an
    //     `iterations` array whose own output_tokens must not be added.
    //   - 248 rows rose above an earlier row of the same id, every one of them
    //     in a subagents/ file, none in a main transcript. Rises measured
    //     include 53 -> 435.
    //   - isApiErrorMessage rows have model "<synthetic>" and 0 output.
    //
    // Run from the console exe and compiled into tests/UnitTests, the same
    // arrangement as TranscriptSuite.
    internal static class LedgerSuite
    {
        internal static List<string> RunAll()
        {
            var failures = new List<string>();

            void Check(string name, bool ok, string detail = "")
            {
                if (ok) return;
                failures.Add(detail.Length > 0 ? $"{name}\n      {detail}" : name);
            }

            var since = DateTimeOffset.Parse("2026-09-25T00:00:00Z");
            var none = Array.Empty<string>();

            // Two rows of one response: the first as the stream opened, the
            // second with the final count — and cache numbers far larger than
            // either, which must not count.
            const string Opened =
                """{"parentUuid":"p1","isSidechain":true,"type":"assistant","message":{"model":"claude-opus","id":"msg_A","type":"message","role":"assistant","content":[{"type":"text","text":"…"}],"usage":{"input_tokens":2,"cache_creation_input_tokens":5846,"cache_read_input_tokens":123507,"output_tokens":53,"iterations":[{"output_tokens":53}]}},"uuid":"r1","timestamp":"2026-09-26T03:40:18.101Z"}""";
            const string Finished =
                """{"parentUuid":"r1","isSidechain":true,"type":"assistant","message":{"model":"claude-opus","id":"msg_A","type":"message","role":"assistant","content":[{"type":"tool_use","name":"Read","input":{}}],"stop_reason":"tool_use","usage":{"input_tokens":2,"cache_creation_input_tokens":5846,"cache_read_input_tokens":123507,"output_tokens":435,"iterations":[{"output_tokens":435}]}},"uuid":"r2","timestamp":"2026-09-26T03:40:19.462Z"}""";
            const string Next =
                """{"parentUuid":"r2","type":"assistant","message":{"id":"msg_B","type":"message","role":"assistant","usage":{"input_tokens":9,"cache_read_input_tokens":40000,"output_tokens":100}},"uuid":"r3","timestamp":"2026-09-26T03:41:00.000Z"}""";
            const string UserRow =
                """{"type":"user","message":{"role":"user","content":"assistant please"},"uuid":"u1","timestamp":"2026-09-26T03:40:30.000Z"}""";
            const string ApiError =
                """{"type":"assistant","isApiErrorMessage":true,"error":"overloaded","message":{"id":"msg_E","model":"<synthetic>","role":"assistant","usage":{"input_tokens":0,"output_tokens":7}},"uuid":"e1","timestamp":"2026-09-26T03:42:00.000Z"}""";

            static ReadOnlyMemory<byte> Bytes(params string[] rows) =>
                Encoding.UTF8.GetBytes(string.Concat(rows.Select(r => r + "\n")));

            var whole = BuddyLedger.Read(
                LedgerFormat.ClaudeCode, LedgerCursor.Start,
                Bytes(Opened, UserRow, Finished, Next, ApiError), since, none);

            Check("a response counts its highest output once, not the sum of its rows",
                whole.OutputTokens == 435 + 100, "got " + whole.OutputTokens);
            Check("credited ids come back in order, once each",
                whole.CreditedMessageIds.SequenceEqual(new[] { "msg_A", "msg_B" }),
                string.Join(",", whole.CreditedMessageIds));

            // The same rows across two reads, split between the two rows of
            // msg_A: the second read has to credit only the rise.
            var first = BuddyLedger.Read(
                LedgerFormat.ClaudeCode, LedgerCursor.Start, Bytes(Opened), since, none);
            var second = BuddyLedger.Read(
                LedgerFormat.ClaudeCode, first.Cursor, Bytes(Finished, Next), since,
                first.CreditedMessageIds.ToHashSet());

            Check("a response straddling two reads credits only its rise the second time",
                first.OutputTokens == 53 && second.OutputTokens == 435 - 53 + 100,
                $"got {first.OutputTokens} then {second.OutputTokens}");
            Check("the open response travels in the cursor",
                first.Cursor.OpenMessageId == "msg_A" && first.Cursor.OpenMessageOutput == 53);

            // A subagent row already credited through the other file.
            var twice = BuddyLedger.Read(
                LedgerFormat.ClaudeCode, LedgerCursor.Start, Bytes(Opened, Finished), since,
                new HashSet<string> { "msg_A" });
            Check("an id credited through another file counts nothing here, rises included",
                twice.OutputTokens == 0, "got " + twice.OutputTokens);

            return failures;
        }
    }
}

using System.Text;
using Xunit;

namespace HatchAI.Tests
{
    // BuddyLedger.Read, one rule per case (CB-195). The rows are the real
    // Claude Code shape trimmed to what the ledger reads — see
    // tests/TranscriptTests/LedgerSuite.cs for what was measured before they
    // were written, and LedgerSuiteTests below for that suite run here.
    //
    // Codex rows are NOT real: Codex is not installed on the dev machine, and
    // the payload.info.total_token_usage shape is from memory (labelled
    // ASSUMED in BuddyLedger). These cases pin what the ledger does with that
    // shape, not that Codex writes it.
    public class BuddyLedgerTests
    {
        private static readonly DateTimeOffset Since = DateTimeOffset.Parse("2026-09-25T00:00:00Z");
        private static readonly IReadOnlyCollection<string> NoIds = Array.Empty<string>();

        private static string Row(string id, long output, string at, string extra = "") =>
            "{\"type\":\"assistant\"" + extra + ",\"message\":{\"id\":\"" + id
            + "\",\"role\":\"assistant\",\"usage\":{\"input_tokens\":5000,\"cache_creation_input_tokens\":37840,"
            + "\"cache_read_input_tokens\":36285,\"output_tokens\":" + output + "}},\"timestamp\":\"" + at + "\"}";

        private static string T(int second) => $"2026-09-26T10:00:{second:00}.000Z";

        private static ReadOnlyMemory<byte> Lines(params string[] rows) =>
            Encoding.UTF8.GetBytes(string.Concat(rows.Select(r => r + "\n")));

        private static LedgerRead Claude(ReadOnlyMemory<byte> chunk, LedgerCursor? cursor = null,
            IReadOnlyCollection<string>? recent = null) =>
            BuddyLedger.Read(LedgerFormat.ClaudeCode, cursor ?? LedgerCursor.Start, chunk, Since, recent ?? NoIds);

        [Fact]
        public void DuplicateRowsWithRisingOutputCountTheHighestOnce()
        {
            var read = Claude(Lines(Row("m1", 4, T(1)), Row("m1", 193, T(2)), Row("m1", 193, T(3))));

            Assert.Equal(193, read.OutputTokens);
            Assert.Equal(new[] { "m1" }, read.CreditedMessageIds);
            Assert.Equal("m1", read.Cursor.OpenMessageId);
            Assert.Equal(193, read.Cursor.OpenMessageOutput);
        }

        [Fact]
        public void AFallingRowOfTheOpenResponseCreditsNothing()
        {
            var read = Claude(Lines(Row("m1", 193, T(1)), Row("m1", 4, T(2))));

            Assert.Equal(193, read.OutputTokens);
            Assert.Equal(193, read.Cursor.OpenMessageOutput);
        }

        [Fact]
        public void TheOpenIdIsCarriedAcrossTwoReadsAndOnlyTheRiseIsCredited()
        {
            var first = Claude(Lines(Row("m1", 4, T(1))));
            var second = Claude(Lines(Row("m1", 193, T(2)), Row("m2", 10, T(3))), first.Cursor,
                first.CreditedMessageIds.ToHashSet());

            Assert.Equal(4, first.OutputTokens);
            Assert.Equal(189 + 10, second.OutputTokens);
            Assert.Equal(new[] { "m1", "m2" }, second.CreditedMessageIds);
            Assert.Equal("m2", second.Cursor.OpenMessageId);
        }

        [Fact]
        public void APartialLastLineIsNotConsumed()
        {
            var complete = Row("m1", 50, T(1)) + "\n";
            var partial = Row("m2", 70, T(2));
            var bytes = Encoding.UTF8.GetBytes(complete + partial[..20]);

            var read = Claude(bytes, LedgerCursor.Start with { Offset = 1000 });

            Assert.Equal(50, read.OutputTokens);
            Assert.Equal(1000 + Encoding.UTF8.GetByteCount(complete), read.Cursor.Offset);
        }

        [Fact]
        public void AChunkWithNoCompleteLineReturnsTheCursorUnchanged()
        {
            var cursor = LedgerCursor.Start with { Offset = 7 };
            var read = Claude(Encoding.UTF8.GetBytes(Row("m1", 50, T(1))), cursor);

            Assert.Same(cursor, read.Cursor);
            Assert.Equal(0, read.OutputTokens);
            Assert.Empty(read.CreditedMessageIds);
        }

        [Fact]
        public void RereadingAfterAShrinkIsStoppedByTheTimestampFloor()
        {
            // The scanner resets Offset to 0 when a file shrinks; the rows it
            // then re-reads are at or before LastTimestamp and credit nothing,
            // even with the recent-id set empty. A genuinely new row does.
            var first = Claude(Lines(Row("m1", 50, T(1)), Row("m2", 60, T(2))));
            var reset = first.Cursor with { Offset = 0, OpenMessageId = null, OpenMessageOutput = 0 };

            var again = Claude(Lines(Row("m1", 50, T(1)), Row("m2", 60, T(2)), Row("m3", 7, T(3))), reset);

            Assert.Equal(7, again.OutputTokens);
            Assert.Equal(new[] { "m3" }, again.CreditedMessageIds);
        }

        [Fact]
        public void RowsAtOrBeforeCountingSinceCreditNothingButAdvanceTheCursor()
        {
            var chunk = Lines(
                Row("old", 900, "2026-09-24T23:59:59.000Z"),
                Row("edge", 800, "2026-09-25T00:00:00.000Z"),
                Row("new", 5, T(1)));

            var read = Claude(chunk);

            Assert.Equal(5, read.OutputTokens);
            Assert.Equal(new[] { "new" }, read.CreditedMessageIds);
            Assert.Equal(chunk.Length, read.Cursor.Offset);
        }

        [Fact]
        public void ARowWithNoTimestampIsStillCountedOnceByItsId()
        {
            const string NoTime =
                "{\"type\":\"assistant\",\"message\":{\"id\":\"m1\",\"usage\":{\"output_tokens\":12}}}";

            var read = Claude(Lines(NoTime, NoTime));

            Assert.Equal(12, read.OutputTokens);
            Assert.Null(read.Cursor.LastTimestamp);
        }

        [Fact]
        public void AnIdAlreadyCreditedThroughAnotherFileCountsNothingIncludingItsRises()
        {
            var read = Claude(Lines(Row("shared", 4, T(1)), Row("shared", 193, T(2)), Row("own", 3, T(3))),
                recent: new HashSet<string> { "shared" });

            Assert.Equal(3, read.OutputTokens);
            Assert.Equal(new[] { "own" }, read.CreditedMessageIds);
        }

        [Fact]
        public void AnIdThatReturnsAfterAnotherIdInTheSameReadIsNotCreditedAgain()
        {
            var read = Claude(Lines(Row("a", 10, T(1)), Row("b", 20, T(2)), Row("a", 30, T(3))));

            Assert.Equal(30, read.OutputTokens);
            Assert.Equal(new[] { "a", "b" }, read.CreditedMessageIds);
        }

        [Fact]
        public void MalformedJsonIsSkippedAndTheRestOfTheChunkStillCounts()
        {
            var read = Claude(Lines(
                "{\"type\":\"assistant\",\"message\":{\"id\":",
                "\"assistant\"",
                "[\"assistant\"]",
                Row("m1", 9, T(1))));

            Assert.Equal(9, read.OutputTokens);
        }

        [Fact]
        public void AnOversizeLineIsSkippedWithoutBeingParsed()
        {
            var huge = Row("big", 1_000, T(1), ",\"pad\":\"" + new string('x', BuddyLedger.MaxLineBytes) + "\"");
            var chunk = Lines(huge, Row("m1", 2, T(2)));

            var read = Claude(chunk);

            Assert.Equal(2, read.OutputTokens);
            Assert.Equal(chunk.Length, read.Cursor.Offset);
        }

        [Fact]
        public void AnApiErrorRowIsNotCounted()
        {
            var read = Claude(Lines(Row("err", 7, T(1), ",\"isApiErrorMessage\":true"), Row("m1", 3, T(2))));

            Assert.Equal(3, read.OutputTokens);
            Assert.Equal(new[] { "m1" }, read.CreditedMessageIds);
        }

        [Fact]
        public void InputAndCacheTokensAreNotCounted()
        {
            // Row() carries 5000 input and 74,125 cache tokens on every row;
            // only the output is the unit (owner decision, CB-195).
            var read = Claude(Lines(Row("m1", 666, T(1))));

            Assert.Equal(666, read.OutputTokens);
        }

        [Theory]
        [InlineData("{\"type\":\"user\",\"message\":{\"role\":\"assistant\",\"id\":\"u\",\"usage\":{\"output_tokens\":5}}}")]
        [InlineData("{\"type\":\"assistant\",\"message\":\"assistant\"}")]
        [InlineData("{\"type\":\"assistant\"}")]
        [InlineData("{\"type\":5,\"note\":\"assistant\"}")]
        [InlineData("{\"type\":\"assistant\",\"message\":{\"id\":\"f\",\"usage\":{\"output_tokens\":1.5}}}")]
        [InlineData("{\"type\":\"assistant\",\"message\":{\"id\":\"z\",\"usage\":{\"input_tokens\":9}}}")]
        [InlineData("{\"type\":\"assistant\",\"message\":{\"usage\":{\"output_tokens\":5}}}")]
        [InlineData("{\"type\":\"assistant\",\"message\":{\"id\":\"n\",\"usage\":\"none\"}}")]
        [InlineData("{\"type\":\"assistant\",\"message\":{\"id\":\"neg\",\"usage\":{\"output_tokens\":-5}}}")]
        [InlineData("{\"type\":\"assistant\",\"message\":{\"id\":\"s\",\"usage\":{\"output_tokens\":\"5\"}},\"timestamp\":\"not a time\"}")]
        public void RowsThatAreNotACountableAssistantResponseEarnNothing(string row)
        {
            Assert.Equal(0, Claude(Lines(row)).OutputTokens);
        }

        // ---- Codex (shape ASSUMED) ----

        private static string TokenCount(long? output, string at = "2026-09-26T10:00:00.000Z") =>
            "{\"timestamp\":\"" + at + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":"
            + (output is null
                ? "null"
                : "{\"total_token_usage\":{\"input_tokens\":90000,\"cached_input_tokens\":80000,\"output_tokens\":"
                  + output + ",\"total_tokens\":99999},\"last_token_usage\":{\"output_tokens\":1}}")
            + ",\"rate_limits\":{}}}";

        private static LedgerRead Codex(ReadOnlyMemory<byte> chunk, LedgerCursor? cursor = null) =>
            BuddyLedger.Read(LedgerFormat.Codex, cursor ?? LedgerCursor.Start, chunk, Since, NoIds);

        [Fact]
        public void CodexCreditsTheDeltaOfTheCumulativeOutput()
        {
            var read = Codex(Lines(TokenCount(100), TokenCount(250), TokenCount(250)));

            Assert.Equal(250, read.OutputTokens);
            Assert.Equal(250, read.Cursor.LastCumulative);
            Assert.Empty(read.CreditedMessageIds);
        }

        [Fact]
        public void CodexCarriesTheCumulativeMarkAcrossReads()
        {
            var first = Codex(Lines(TokenCount(100)));
            var second = Codex(Lines(TokenCount(140)), first.Cursor);

            Assert.Equal(40, second.OutputTokens);
        }

        [Fact]
        public void CodexALaterTimestampMovesLastTimestampForward()
        {
            var first = Codex(Lines(TokenCount(10, "2026-09-26T10:00:00.000Z")));
            var second = Codex(Lines(TokenCount(15, "2026-09-26T11:00:00.000Z")), first.Cursor);

            Assert.Equal(5, second.OutputTokens);
            Assert.Equal(DateTimeOffset.Parse("2026-09-26T11:00:00Z"), second.Cursor.LastTimestamp);
        }

        [Fact]
        public void BlankLinesAreSkipped()
        {
            var read = Claude(Encoding.UTF8.GetBytes("\n\n" + Row("m1", 3, T(1)) + "\n\n"));

            Assert.Equal(3, read.OutputTokens);
        }

        [Fact]
        public void CodexNullInfoCountsNothing()
        {
            var read = Codex(Lines(TokenCount(null), TokenCount(30)));

            Assert.Equal(30, read.OutputTokens);
        }

        [Fact]
        public void CodexATotalThatGoesBackwardsCreditsNothingAndKeepsTheHighMark()
        {
            var read = Codex(Lines(TokenCount(500), TokenCount(100), TokenCount(520)));

            Assert.Equal(520, read.OutputTokens);
            Assert.Equal(520, read.Cursor.LastCumulative);
        }

        [Fact]
        public void CodexTotalsBeforeCountingSinceMoveTheMarkButCreditNothing()
        {
            var read = Codex(Lines(
                TokenCount(1_000, "2026-09-24T10:00:00.000Z"),
                TokenCount(1_200, "2026-09-26T10:00:00.000Z")));

            Assert.Equal(200, read.OutputTokens);
            Assert.Equal(1_200, read.Cursor.LastCumulative);
            Assert.Equal(DateTimeOffset.Parse("2026-09-26T10:00:00Z"), read.Cursor.LastTimestamp);
        }

        [Fact]
        public void CodexATotalWithNoTimestampStillCounts()
        {
            var row = TokenCount(40).Replace("\"timestamp\":\"2026-09-26T10:00:00.000Z\",", "");

            var read = Codex(Lines(row));

            Assert.Equal(40, read.OutputTokens);
            Assert.Null(read.Cursor.LastTimestamp);
        }

        [Fact]
        public void CodexAnOlderTimestampDoesNotMoveLastTimestampBack()
        {
            var cursor = LedgerCursor.Start with { LastTimestamp = DateTimeOffset.Parse("2026-09-27T00:00:00Z") };

            var read = Codex(Lines(TokenCount(10)), cursor);

            Assert.Equal(10, read.OutputTokens);
            Assert.Equal(cursor.LastTimestamp, read.Cursor.LastTimestamp);
        }

        [Theory]
        [InlineData("{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{}}}")]
        [InlineData("{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":7}}}")]
        [InlineData("{\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_message\",\"note\":\"token_count\"}}")]
        [InlineData("{\"type\":\"event_msg\",\"payload\":\"token_count\"}")]
        [InlineData("{\"type\":\"token_count\"}")]
        [InlineData("{\"payload\":{\"type\":\"token_count\"}}")]
        [InlineData("[\"token_count\"]")]
        [InlineData("{\"token_count\": ")]
        public void CodexRowsThatAreNotARunningTotalEarnNothing(string row)
        {
            Assert.Equal(0, Codex(Lines(row)).OutputTokens);
        }

        // ---- the recent-id set ----

        [Fact]
        public void FoldRecentAppendsNewIdsOnceAndTrimsTheOldest()
        {
            var folded = BuddyLedger.FoldRecent(new[] { "a", "b", "c" }, new[] { "c", "d", "e", "d" }, limit: 4);

            Assert.Equal(new[] { "b", "c", "d", "e" }, folded);
        }

        [Fact]
        public void FoldRecentUnderTheLimitKeepsEverything()
        {
            Assert.Equal(new[] { "a", "b" }, BuddyLedger.FoldRecent(new[] { "a" }, new[] { "b" }));
        }

        // ---- overflow (QA on CB-195) ----

        // Two responses of long.MaxValue each used to wrap to -2, which the
        // controller then dropped as "nothing earned" while the cursors that
        // read them still advanced — the credit lost for good. Saturated, the
        // sum is the largest number there is, and the rise path the same.
        [Fact]
        public void OutputPastLongMaxValueSaturatesOnBothTheNewIdAndTheRisePaths()
        {
            var read = Claude(Lines(
                Row("big", long.MaxValue, T(1)),
                Row("m1", 1, T(2)),
                Row("m1", long.MaxValue, T(3)),
                Row("big2", long.MaxValue, T(4))));

            Assert.Equal(long.MaxValue, read.OutputTokens);
        }

        [Theory]
        [InlineData(1L, 2L, 3L)]
        [InlineData(long.MaxValue, 1L, long.MaxValue)]
        [InlineData(1L, long.MaxValue, long.MaxValue)]
        [InlineData(long.MaxValue, long.MaxValue, long.MaxValue)]
        [InlineData(long.MinValue, -1L, long.MinValue)]
        [InlineData(-5L, 3L, -2L)]
        [InlineData(long.MaxValue, long.MinValue, -1L)]
        public void AddSaturatesInBothDirections(long a, long b, long expected) =>
            Assert.Equal(expected, BuddyLedger.Add(a, b));
    }

    // LedgerSuite's real-shape cases, run here so they count toward coverage;
    // the same arrangement as TranscriptSuiteTests.
    public class LedgerSuiteTests
    {
        [Fact]
        public void EveryLedgerSuiteCasePasses()
        {
            var failures = LedgerSuite.RunAll();

            Assert.True(failures.Count == 0,
                $"{failures.Count} ledger cases failed:\n  ✗ " + string.Join("\n  ✗ ", failures));
        }
    }
}

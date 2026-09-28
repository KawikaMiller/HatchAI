namespace HatchAI
{
    // Everything about the buddy that has to survive a restart — the part the
    // user earned, as opposed to BuddyGenome, the part a hash decided. It is
    // what BuddyStore reads out of and writes into the top-level "buddy"
    // object in settings.json.
    //
    // The ledger data lives here, beside the counts, on purpose: cursors and
    // token totals are saved in one atomic write, so a crash between reading a
    // transcript and saving leaves both behind together and the next run
    // re-reads rather than double-counts. Split them into two writes and that
    // guarantee is gone.
    //
    // Immutable: every change is a `with` that produces the next state, which
    // is what lets BuddyProgress and BuddyLedger stay pure.
    internal sealed record BuddyState(
        // Random, generated once at first hatch, never regenerated. Deleting
        // settings.json is the only way to get a new one (accepted trade-off).
        string Uuid,
        // How many times the user has been reborn. Also the second hatch input.
        int Rebirths,
        // Transcript activity before this instant is never counted, so a new
        // install does not hatch straight into a third-stage buddy off years
        // of old history. Set at first hatch; not moved by a rebirth, since
        // the cursors already stand past everything counted.
        DateTimeOffset CountingSince,
        // Output tokens earned by the current buddy. Resets to 0 on rebirth.
        long Tokens,
        // Output tokens across every buddy this uuid has had. Never resets,
        // and is added to in the same step as Tokens so the two cannot drift.
        long LifetimeTokens,
        // A user-chosen name, or null to use the genome's rolled name.
        string? Name,
        // Stars the current buddy has earned (0-5; 60k/120k/240k/480k/960k).
        // Always equal to BuddyProgress.StarsFor(Tokens) — stored so the card
        // and history can show it without a genome, not as a second source of
        // truth. If they ever disagree, StarsFor wins.
        int Stars,
        // Past buddies, oldest first. Appended to by a rebirth; never trimmed.
        IReadOnlyList<BuddyHistoryEntry> History,
        // One cursor per transcript file, keyed by absolute path.
        IReadOnlyDictionary<string, LedgerCursor> Cursors,
        // Recently credited message ids, bounded, newest last. Guards against
        // the same response appearing in two files (2 in 5,739 measured).
        IReadOnlyList<string> RecentMessageIds)
    {
        // A state with nothing earned yet, for a first hatch.
        internal static BuddyState Hatch(string uuid, DateTimeOffset now) => new(
            uuid, 0, now, 0, 0, null, 0,
            Array.Empty<BuddyHistoryEntry>(),
            new Dictionary<string, LedgerCursor>(),
            Array.Empty<string>());
    }

    // A buddy that was reborn away. A snapshot of what it looked like rather
    // than just its hatch inputs: re-rolling (uuid, Rebirth) would work today,
    // and stop working the day the hatch salt moves to v2.
    internal sealed record BuddyHistoryEntry(
        int Rebirth,
        string Name,
        BuddySpecies Species,
        BuddyRarity Rarity,
        bool Shiny,
        BuddyStage Stage,
        int Stars,
        long Tokens,
        DateTimeOffset RetiredAt);

    // Where BuddyLedger stopped reading one transcript file.
    //
    // Offset only ever points just past a complete line: a half-written last
    // line is left for the next read, not parsed and not skipped.
    //
    // Claude Code writes one response as several rows sharing a message.id,
    // some with rising output_tokens, so the rule is "per id, the highest
    // output seen, never the sum" — and a response can straddle two reads.
    // OpenMessageId/OpenMessageOutput carry the one still-open response across
    // that boundary so the next read credits only the rise.
    //
    // LastCumulative is Codex's: its rollouts report a running
    // total_token_usage, so the credit is the delta from the last total seen.
    // Always 0 for a Claude Code file. (The Codex field shape is ASSUMED — see
    // docs/buddy-design.md.)
    internal sealed record LedgerCursor(
        long Offset,
        DateTimeOffset? LastTimestamp,
        string? OpenMessageId,
        long OpenMessageOutput,
        long LastCumulative)
    {
        internal static readonly LedgerCursor Start = new(0, null, null, 0, 0);
    }
}

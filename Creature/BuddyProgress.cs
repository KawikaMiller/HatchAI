namespace HatchAI
{
    // What earned tokens mean: stage, stars, whether rebirth is offered, and
    // the two state changes progress makes. Owned by E1 (CB-195). Pure — the
    // clock is a parameter.
    //
    // The unit is OUTPUT tokens only (owner decision on CB-195). Input and
    // cache tokens were measured crossing 10k inside a single turn, which
    // would make an evolution meaningless.
    internal static class BuddyProgress
    {
        // Where each stage begins, indexed by BuddyStage: an egg from 0, which
        // hatches at 2.5k, then one evolution per 10k after that (owner
        // decision on CB-195, which added the egg). The egg's gap is the one
        // short one, so a new buddy — and every reborn one — gets its first
        // moment inside a session or two rather than a whole 10k in.
        internal static readonly long[] StageThresholds = { 0, 2_500, 12_500, 22_500, 32_500 };

        // Rebirth is offered one further 10k after Third begins, not the
        // moment Third arrives: the owner wanted something still to work
        // towards once the last stage is reached (CB-195). Before the egg,
        // this was Third's own threshold; the two are separate on purpose now.
        internal const long RebirthThreshold = 42_500;

        // Veteran stars for a buddy kept past its third evolution. Max 5.
        internal static readonly long[] StarThresholds = { 60_000, 120_000, 240_000, 480_000, 960_000 };

        // The last stage whose threshold has been reached. A negative count
        // cannot come out of the ledger, but a hand-edited settings.json can
        // hold one, and it reads as an egg rather than as an enum value
        // nothing can draw.
        internal static BuddyStage StageFor(long tokens)
        {
            var stage = 0;
            while (stage + 1 < StageThresholds.Length && tokens >= StageThresholds[stage + 1]) stage++;
            return (BuddyStage)stage;
        }

        internal static int StarsFor(long tokens)
        {
            var stars = 0;
            while (stars < StarThresholds.Length && tokens >= StarThresholds[stars]) stars++;
            return stars;
        }

        // True from RebirthThreshold on, and stays true: the offer is
        // standing and non-destructive, never a one-time prompt.
        internal static bool CanRebirth(long tokens) => tokens >= RebirthThreshold;

        // Every count at which something is earned, in order: each stage
        // after the egg, the rebirth offer, then the stars. The card measures
        // its progress bar between two of these.
        internal static IEnumerable<long> Milestones() =>
            StageThresholds.Skip(1).Append(RebirthThreshold).Concat(StarThresholds);

        // Tokens at which the next stage, the rebirth offer or a star lands,
        // or null past the last. There is nothing between the rebirth offer
        // at 42.5k and the first star at 60k, which is the "milestone spacing
        // after the 3rd evolution" the plan settled.
        internal static long? NextMilestone(long tokens)
        {
            foreach (var milestone in Milestones())
                if (tokens < milestone) return milestone;
            return null;
        }

        // Adds to Tokens and LifetimeTokens in one step and refreshes Stars.
        //
        // A zero or negative credit returns the state untouched: the ledger
        // never produces one, and letting it through would be the one way the
        // two totals could be walked backwards.
        internal static BuddyState Credit(BuddyState state, long outputTokens)
        {
            if (outputTokens <= 0) return state;

            // Saturating, not wrapping: see BuddyLedger.Add.
            var tokens = BuddyLedger.Add(state.Tokens, outputTokens);
            return state with
            {
                Tokens = tokens,
                LifetimeTokens = BuddyLedger.Add(state.LifetimeTokens, outputTokens),
                Stars = StarsFor(tokens),
            };
        }

        // Appends the current buddy to History (snapshotting `current`, the
        // genome it was rolled as), bumps Rebirths, resets Tokens and Stars
        // and clears Name. Leaves LifetimeTokens, cursors and the uuid alone.
        // Refuses (returns `state` unchanged) when CanRebirth is false, and at
        // int.MaxValue rebirths — only a hand-edited settings.json gets there,
        // and wrapping would hand the next buddy a negative rebirth count,
        // a hatch input nothing was ever rolled with (QA on CB-195).
        //
        // The history entry carries the name the user actually saw — their own
        // if they chose one — and stage and stars computed from Tokens rather
        // than copied from the stored Stars, since StarsFor is the authority
        // if the two ever disagree.
        internal static BuddyState Rebirth(BuddyState state, BuddyGenome current, DateTimeOffset now)
        {
            if (!CanRebirth(state.Tokens) || state.Rebirths == int.MaxValue) return state;
            var retired = new BuddyHistoryEntry(
                state.Rebirths,
                state.Name ?? current.Name,
                current.Species,
                current.Rarity,
                current.Shiny,
                StageFor(state.Tokens),
                StarsFor(state.Tokens),
                state.Tokens,
                now);
            return state with
            {
                Rebirths = state.Rebirths + 1,
                Tokens = 0,
                Stars = 0,
                Name = null,
                History = state.History.Append(retired).ToArray(),
            };
        }
    }
}

using Xunit;

namespace HatchAI.Tests;

// What tokens are worth. The boundaries are the whole of this file's risk:
// an off-by-one at 2,500 or 42,500 is a buddy that hatches a response early
// or offers rebirth a stage late, and nothing on screen says which.
public class BuddyProgressTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    // An egg to 2,500, then a stage per 10k, capped at Third (CB-195).
    [Theory]
    [InlineData(-15_000, "Egg")]
    [InlineData(-1, "Egg")]
    [InlineData(0, "Egg")]
    [InlineData(2_499, "Egg")]
    [InlineData(2_500, "Hatchling")]
    [InlineData(12_499, "Hatchling")]
    [InlineData(12_500, "First")]
    [InlineData(22_499, "First")]
    [InlineData(22_500, "Second")]
    [InlineData(32_499, "Second")]
    [InlineData(32_500, "Third")]
    [InlineData(42_499, "Third")]
    [InlineData(42_500, "Third")]
    [InlineData(5_000_000, "Third")]
    [InlineData(long.MaxValue, "Third")]
    public void AnEggThenAStagePerTenThousandCappedAtThird(long tokens, string expected)
    {
        Assert.Equal(Enum.Parse<BuddyStage>(expected), BuddyProgress.StageFor(tokens));
    }

    // The table the thresholds are read from, pinned as a whole: one entry
    // per stage, in order, starting from nothing.
    [Fact]
    public void ThereIsAThresholdForEveryStage()
    {
        Assert.Equal(new long[] { 0, 2_500, 12_500, 22_500, 32_500 }, BuddyProgress.StageThresholds);
        Assert.Equal(Enum.GetValues<BuddyStage>().Length, BuddyProgress.StageThresholds.Length);
        for (var i = 0; i < BuddyProgress.StageThresholds.Length; i++)
            Assert.Equal((BuddyStage)i, BuddyProgress.StageFor(BuddyProgress.StageThresholds[i]));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(59_999, 0)]
    [InlineData(60_000, 1)]
    [InlineData(119_999, 1)]
    [InlineData(120_000, 2)]
    [InlineData(240_000, 3)]
    [InlineData(480_000, 4)]
    [InlineData(959_999, 4)]
    [InlineData(960_000, 5)]
    [InlineData(long.MaxValue, 5)]
    public void StarsLandOnTheThresholdsAndStopAtFive(long tokens, int expected)
    {
        Assert.Equal(expected, BuddyProgress.StarsFor(tokens));
    }

    // Not the moment Third arrives, but a full 10k after it: reaching the
    // last stage leaves the offer still to work towards (owner's correction
    // on CB-195). 32,500 is the case that pins the two apart.
    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(2_500, false)]
    [InlineData(12_500, false)]
    [InlineData(22_500, false)]
    [InlineData(32_499, false)]
    [InlineData(32_500, false)]
    [InlineData(42_499, false)]
    [InlineData(42_500, true)]
    [InlineData(1_000_000, true)]
    [InlineData(long.MaxValue, true)]
    public void RebirthIsOfferedATenThousandPastTheThirdEvolution(long tokens, bool expected)
    {
        Assert.Equal(expected, BuddyProgress.CanRebirth(tokens));
    }

    [Theory]
    [InlineData(-500, 2_500L)]
    [InlineData(0, 2_500L)]
    [InlineData(2_499, 2_500L)]
    [InlineData(2_500, 12_500L)]
    [InlineData(12_499, 12_500L)]
    [InlineData(12_500, 22_500L)]
    [InlineData(22_499, 22_500L)]
    [InlineData(22_500, 32_500L)]
    [InlineData(32_499, 32_500L)]
    [InlineData(32_500, 42_500L)]
    [InlineData(42_499, 42_500L)]
    [InlineData(42_500, 60_000L)]
    [InlineData(59_999, 60_000L)]
    [InlineData(60_000, 120_000L)]
    [InlineData(959_999, 960_000L)]
    [InlineData(960_000, null)]
    [InlineData(10_000_000, null)]
    public void NextMilestoneWalksStagesThenRebirthThenStarsThenStops(long tokens, long? expected)
    {
        Assert.Equal(expected, BuddyProgress.NextMilestone(tokens));
    }

    [Fact]
    public void MilestonesAreEveryStageAfterTheEggThenRebirthThenTheStars()
    {
        Assert.Equal(
            new long[] { 2_500, 12_500, 22_500, 32_500, 42_500, 60_000, 120_000, 240_000, 480_000, 960_000 },
            BuddyProgress.Milestones());
    }

    // --- Credit ----------------------------------------------------------

    [Fact]
    public void CreditMovesTokensAndLifetimeTogetherAndRefreshesStars()
    {
        var s = BuddyState.Hatch("u", T0);
        s = BuddyProgress.Credit(s, 2_499);
        Assert.Equal(2_499, s.Tokens);
        Assert.Equal(2_499, s.LifetimeTokens);
        Assert.Equal(0, s.Stars);

        s = BuddyProgress.Credit(s, 57_501);
        Assert.Equal(60_000, s.Tokens);
        Assert.Equal(60_000, s.LifetimeTokens);
        Assert.Equal(1, s.Stars);
    }

    [Fact]
    public void LifetimeEqualsTokensUntilTheFirstRebirth()
    {
        var s = BuddyState.Hatch("u", T0);
        foreach (var n in new long[] { 1, 666, 9_332, 1, 20_000, 123_456 })
        {
            s = BuddyProgress.Credit(s, n);
            Assert.Equal(s.Tokens, s.LifetimeTokens);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ANonPositiveCreditChangesNothing(long n)
    {
        var s = BuddyProgress.Credit(BuddyState.Hatch("u", T0), 100);
        Assert.Same(s, BuddyProgress.Credit(s, n));
    }

    // --- Rebirth ---------------------------------------------------------

    [Fact]
    public void RebirthResetsTheBuddyAndKeepsEverythingElse()
    {
        var cursors = new Dictionary<string, LedgerCursor> { ["/t.jsonl"] = new(123, T0, "m", 4, 0) };
        var start = BuddyProgress.Credit(BuddyState.Hatch("uuid-1", T0), 64_321) with
        {
            Name = "Sir Quackers",
            Cursors = cursors,
            RecentMessageIds = new[] { "m1", "m2" },
        };
        var genome = BuddyHatch.Roll("uuid-1", 0);
        var later = T0.AddDays(3);

        var reborn = BuddyProgress.Rebirth(start, genome, later);

        Assert.Equal(1, reborn.Rebirths);
        Assert.Equal(0, reborn.Tokens);
        Assert.Equal(0, reborn.Stars);
        Assert.Null(reborn.Name);
        Assert.Equal(64_321, reborn.LifetimeTokens);
        Assert.Equal("uuid-1", reborn.Uuid);
        Assert.Equal(T0, reborn.CountingSince);
        Assert.Same(cursors, reborn.Cursors);
        Assert.Equal(start.RecentMessageIds, reborn.RecentMessageIds);

        var entry = Assert.Single(reborn.History);
        Assert.Equal(new BuddyHistoryEntry(
            0, "Sir Quackers", genome.Species, genome.Rarity, genome.Shiny,
            BuddyStage.Third, 1, 64_321, later), entry);
    }

    [Fact]
    public void HistoryAppendsOldestFirstAndUsesTheRolledNameWhenNoneWasChosen()
    {
        var s = BuddyProgress.Credit(BuddyState.Hatch("u", T0), 42_500);
        var g0 = BuddyHatch.Roll("u", 0);
        s = BuddyProgress.Rebirth(s, g0, T0);

        s = BuddyProgress.Credit(s, 45_000);
        var g1 = BuddyHatch.Roll("u", 1);
        s = BuddyProgress.Rebirth(s, g1, T0.AddHours(1));

        Assert.Equal(2, s.Rebirths);
        Assert.Equal(87_500, s.LifetimeTokens);
        Assert.Equal(new[] { 0, 1 }, s.History.Select(h => h.Rebirth));
        Assert.Equal(new[] { g0.Name, g1.Name }, s.History.Select(h => h.Name));
        Assert.Equal(new long[] { 42_500, 45_000 }, s.History.Select(h => h.Tokens));
    }

    // Birth and rebirth both give an egg (CB-195): the reborn buddy's count
    // is back to zero, which is the egg's own threshold.
    [Fact]
    public void ARebornBuddyIsAnEgg()
    {
        var s = BuddyProgress.Credit(BuddyState.Hatch("u", T0), 50_000);
        Assert.Equal(BuddyStage.Third, BuddyProgress.StageFor(s.Tokens));

        var reborn = BuddyProgress.Rebirth(s, BuddyHatch.Roll("u", 0), T0);

        Assert.Equal(BuddyStage.Egg, BuddyProgress.StageFor(reborn.Tokens));
        Assert.Equal(BuddyStage.Third, Assert.Single(reborn.History).Stage);
    }

    // Including the whole of Third's first 10k: 32,500 is Third, and still
    // not enough.
    [Theory]
    [InlineData(0)]
    [InlineData(32_500)]
    [InlineData(42_499)]
    public void RebirthBeforeTheThresholdIsRefused(long tokens)
    {
        var s = BuddyState.Hatch("u", T0);
        if (tokens > 0) s = BuddyProgress.Credit(s, tokens);
        Assert.Same(s, BuddyProgress.Rebirth(s, BuddyHatch.Roll("u", 0), T0));
    }

    // --- the edges of the number line (QA on CB-195) -----------------------

    [Fact]
    public void ACreditPastLongMaxValueSaturatesRatherThanGoingNegative()
    {
        var s = BuddyState.Hatch("u", T0) with { Tokens = long.MaxValue - 5, LifetimeTokens = long.MaxValue - 1 };

        var credited = BuddyProgress.Credit(s, 10);

        Assert.Equal(long.MaxValue, credited.Tokens);
        Assert.Equal(long.MaxValue, credited.LifetimeTokens);
        Assert.Equal(5, credited.Stars);
    }

    // Only a hand-edited settings.json gets here. Wrapping would hand the
    // next buddy a negative rebirth count — a hatch input nothing was ever
    // rolled with — so the offer is simply refused at the end of the line.
    [Fact]
    public void RebirthAtTheLastRebirthCountIsRefusedRatherThanWrapping()
    {
        // Past the rebirth threshold, so the count is the only reason to refuse.
        var s = BuddyProgress.Credit(BuddyState.Hatch("u", T0), 42_500) with { Rebirths = int.MaxValue };

        Assert.Same(s, BuddyProgress.Rebirth(s, BuddyHatch.Roll("u", int.MaxValue), T0));
    }
}

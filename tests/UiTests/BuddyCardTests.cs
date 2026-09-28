using System;
using System.Linq;
using Avalonia.Headless.XUnit;
using Xunit;

namespace HatchAI.Tests;

// The card's words: what it says about the buddy, how far it has to go, the
// lifetime total and the buddies that came before. Asserted on the text a
// person reads.
[Collection("Settings")]
public class BuddyCardTests
{
    private static readonly DateTimeOffset Retired = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

    private static BuddyCard Card(BuddyGenome genome, BuddyState state)
    {
        var card = new BuddyCard();
        card.UpdateFrom(genome, state);
        return card;
    }

    [AvaloniaFact]
    public void TheHeaderSaysWhoAndWhat()
    {
        var card = Card(BuddyWindowTests.Genome(BuddySpecies.Axolotl, BuddyRarity.Epic, shiny: true), BuddyWindowTests.State(65_000));

        Assert.Equal("Pip", card.NameShown);
        Assert.Equal("Shiny Epic Axolotl", card.KindShown);
        Assert.Equal("Third evolution ★", card.StageShown);
        Assert.Equal("Cheerful · Curious", card.PersonalityShown);
        Assert.Equal(BuddyStage.Third, card.PortraitControl.Stage);
    }

    [AvaloniaFact]
    public void FiveStatsInOrderWithTheirValues()
    {
        var card = Card(BuddyWindowTests.Genome(), BuddyWindowTests.State(0));

        Assert.Equal(
            new[] { "DEBUGGING 80", "PATIENCE 40", "CHAOS 12", "WISDOM 55", "SNARK 60" },
            card.StatsShown);
    }

    // Each stage's own readout (CB-195): an egg counts down to hatching, each
    // evolution to the next, Third to the rebirth offer a further 10k on,
    // and only then the stars. The bar measures from the milestone just
    // passed, so it restarts at every one of those.
    [AvaloniaTheory]
    [InlineData(0, "Hatches at 2.5k", "0 / 2.5k", 0.0)]
    [InlineData(1_250, "Hatches at 2.5k", "1.25k / 2.5k", 0.5)]
    [InlineData(2_499, "Hatches at 2.5k", "2.49k / 2.5k", 0.9996)]
    [InlineData(2_500, "First evolution at 12.5k", "2.5k / 12.5k", 0.0)]
    [InlineData(12_345, "First evolution at 12.5k", "12.3k / 12.5k", 0.9845)]
    [InlineData(12_500, "Second evolution at 22.5k", "12.5k / 22.5k", 0.0)]
    [InlineData(22_500, "Third evolution at 32.5k", "22.5k / 32.5k", 0.0)]
    [InlineData(32_499, "Third evolution at 32.5k", "32.4k / 32.5k", 0.9999)]
    [InlineData(32_500, "Rebirth available at 42.5k", "32.5k / 42.5k", 0.0)]
    [InlineData(37_500, "Rebirth available at 42.5k", "37.5k / 42.5k", 0.5)]
    [InlineData(42_499, "Rebirth available at 42.5k", "42.4k / 42.5k", 0.9999)]
    [InlineData(42_500, "Veteran star 1 at 60k", "42.5k / 60k", 0.0)]
    [InlineData(90_000, "Veteran star 2 at 120k", "90k / 120k", 0.5)]
    [InlineData(960_000, "Every star earned", "960k", 1.0)]
    public void ProgressToTheNextMilestone(long tokens, string next, string progress, double fraction)
    {
        var card = Card(BuddyWindowTests.Genome(), BuddyWindowTests.State(tokens));

        Assert.Equal(next, card.NextShown);
        Assert.Equal(progress, card.ProgressShown);
        Assert.Equal(fraction, card.ProgressFraction, 4);
    }

    [AvaloniaFact]
    public void LifetimeTotalWithTheExactNumberOnHover()
    {
        var history = new[]
        {
            new BuddyHistoryEntry(0, "Mochi", BuddySpecies.Cat, BuddyRarity.Common, false, BuddyStage.Third, 0, 43_200, Retired),
            new BuddyHistoryEntry(1, "Blip", BuddySpecies.Robot, BuddyRarity.Legendary, true, BuddyStage.Third, 3, 250_000, Retired.AddDays(9)),
        };

        var card = Card(BuddyWindowTests.Genome(), BuddyWindowTests.State(941_367, 1_234_567, null, history));

        Assert.Equal("Lifetime: 1.23M tokens · 3 buddies", card.LifetimeShown);
        Assert.Equal("1,234,567 output tokens across 3 buddies", card.LifetimeTip);
    }

    [AvaloniaFact]
    public void OneBuddySoFarIsSingular()
    {
        var card = Card(BuddyWindowTests.Genome(), BuddyWindowTests.State(950));

        Assert.Equal("Lifetime: 950 tokens · 1 buddy", card.LifetimeShown);
        Assert.Empty(card.HistoryShown);
    }

    [AvaloniaFact]
    public void HistoryNewestFirstWithFinalTokensStageAndStars()
    {
        var history = new[]
        {
            new BuddyHistoryEntry(0, "Mochi", BuddySpecies.Cat, BuddyRarity.Common, false, BuddyStage.Third, 0, 43_200, Retired),
            new BuddyHistoryEntry(1, "Blip", BuddySpecies.Robot, BuddyRarity.Legendary, true, BuddyStage.Third, 3, 250_000, Retired.AddDays(9)),
        };

        var card = Card(BuddyWindowTests.Genome(), BuddyWindowTests.State(0, 293_200, null, history));

        Assert.Equal(
            new[]
            {
                "Blip  Shiny Legendary Robot · Third ★★★ 250k",
                "Mochi  Common Cat · Third 43.2k",
            },
            card.HistoryShown);
    }

    [AvaloniaFact]
    public void StageNamesAndLines()
    {
        Assert.Equal("Egg", BuddyCard.StageName(BuddyStage.Egg));
        Assert.Equal("Hatchling", BuddyCard.StageName(BuddyStage.Hatchling));
        Assert.Equal("First evolution", BuddyCard.StageName(BuddyStage.First));
        Assert.Equal("Second evolution", BuddyCard.StageLine(BuddyStage.Second, 0));
        Assert.Equal("Third evolution ★★★★★", BuddyCard.StageLine(BuddyStage.Third, 5));
        Assert.Null(BuddyCard.ProgressSpan(long.MaxValue));
        Assert.Equal((0L, 2_500L), BuddyCard.ProgressSpan(-10));
        Assert.Equal((22_500L, 32_500L), BuddyCard.ProgressSpan(25_000));
        Assert.Equal((32_500L, 42_500L), BuddyCard.ProgressSpan(40_000));
    }

    // An egg's card: the egg in the portrait, "Egg" for a stage, and a bar
    // counting down to the hatch. No rebirth offer, of course.
    [AvaloniaFact]
    public void AnEggsCardCountsDownToHatching()
    {
        var card = Card(BuddyWindowTests.Genome(), BuddyWindowTests.State(1_000));

        Assert.Equal("Egg", card.StageShown);
        Assert.Equal(BuddyStage.Egg, card.PortraitControl.Stage);
        Assert.Equal("Hatches at 2.5k", card.NextShown);
        Assert.Equal("1k / 2.5k", card.ProgressShown);
        Assert.False(card.OffersRebirth);
    }

    // The owner's correction on CB-195: reaching Third is not reaching the
    // offer. The card at Third says what is still to come, and offers it
    // only once it has come.
    [AvaloniaTheory]
    [InlineData(32_500, false)]
    [InlineData(42_499, false)]
    [InlineData(42_500, true)]
    public void ThirdAloneDoesNotOfferRebirth(long tokens, bool offered)
    {
        var card = Card(BuddyWindowTests.Genome(), BuddyWindowTests.State(tokens));

        Assert.Equal("Third evolution", card.StageShown);
        Assert.Equal(offered, card.OffersRebirth);
    }
}

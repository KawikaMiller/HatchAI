using System.Globalization;
using Xunit;

namespace HatchAI.Tests;

// The hatch is the one mapping in the buddy that must never move by accident:
// a user's buddy is a pure function of (uuid, rebirths), re-rolled on every
// load, so any change to the hash, the slice layout, a table or an enum's
// order silently hands every existing user a different creature. Nothing
// throws when that happens — which is why the goldens below exist.
public class BuddyHatchTests
{
    // --- golden: the mapping itself ------------------------------------

    // These rows were GENERATED from BuddyHatch.Roll as first written for
    // CB-195 and then frozen; they are not hand-derived. They pin what real
    // users got. If one fails, the change is to the hatch, and the fix is a
    // new salt version (claude-buddy/hatch/v2) with its own goldens — never
    // regenerating these to make them pass.
    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000", 0, "Cactus", "Common", false, "Cross", "None", 16, 93, 21, 38, 44, "Chaotic", "Curious", "Nimto")]
    [InlineData("00000000-0000-0000-0000-000000000000", 1, "Turtle", "Uncommon", false, "Ring", "Crown", 61, 36, 80, 28, 44, "Chaotic", "Zen", "Luber")]
    [InlineData("00000000-0000-0000-0000-000000000000", 2, "Dragon", "Common", false, "Cross", "None", 75, 52, 14, 25, 36, "Sleepy", "Sassy", "Ruedle")]
    [InlineData("00000000-0000-0000-0000-000000000000", 3, "Goose", "Epic", false, "Spiral", "Propeller", 51, 51, 48, 86, 62, "Sleepy", "Chaotic", "Nimmo")]
    [InlineData("8f14e45f-ceea-467a-9575-9a8a7b8e2c11", 0, "Turtle", "Common", false, "Ring", "None", 53, 39, 19, 33, 95, "Cheerful", "Curious", "Luster")]
    [InlineData("8f14e45f-ceea-467a-9575-9a8a7b8e2c11", 1, "Robot", "Common", false, "Ring", "None", 24, 31, 10, 39, 95, "Chaotic", "Cheerful", "Zubo")]
    [InlineData("8f14e45f-ceea-467a-9575-9a8a7b8e2c11", 2, "Duck", "Common", false, "Dot", "None", 56, 33, 74, 95, 14, "Zen", "Sleepy", "Kizle")]
    [InlineData("8f14e45f-ceea-467a-9575-9a8a7b8e2c11", 3, "Mushroom", "Uncommon", false, "Star", "Wizard", 45, 35, 88, 25, 42, "Anxious", "Sassy", "Bixby")]
    [InlineData("c0ffee00-1234-4abc-8def-0123456789ab", 0, "Cactus", "Uncommon", false, "Dot", "Halo", 98, 17, 66, 47, 38, "Sassy", "Grumpy", "Pipra")]
    [InlineData("c0ffee00-1234-4abc-8def-0123456789ab", 1, "Goose", "Common", false, "Cross", "None", 23, 22, 39, 8, 75, "Zen", "Chaotic", "Nimby")]
    [InlineData("c0ffee00-1234-4abc-8def-0123456789ab", 2, "Turtle", "Common", false, "Cross", "None", 92, 13, 64, 62, 62, "Sleepy", "Sassy", "Tamni")]
    [InlineData("c0ffee00-1234-4abc-8def-0123456789ab", 3, "Dragon", "Uncommon", false, "Star", "TopHat", 28, 43, 73, 99, 71, "Sassy", "Chaotic", "Gusdle")]
    public void TheHatchMappingIsPinned(
        string uuid, int rebirths, string species, string rarity, bool shiny, string eyes, string hat,
        int debugging, int patience, int chaos, int wisdom, int snark,
        string primary, string secondary, string name)
    {
        // Names rather than enum values in the rows: the enums are internal,
        // and a public theory cannot take them as parameters.
        var kind = Enum.Parse<BuddySpecies>(species);
        var expected = new BuddyGenome(
            kind, BuddyTaxonomy.FamilyOf(kind), Enum.Parse<BuddyRarity>(rarity), shiny,
            Enum.Parse<BuddyEyes>(eyes), Enum.Parse<BuddyHat>(hat),
            new BuddyStats(debugging, patience, chaos, wisdom, snark),
            Enum.Parse<BuddyPersonality>(primary), Enum.Parse<BuddyPersonality>(secondary), name);

        Assert.Equal(expected, BuddyHatch.Roll(uuid, rebirths));
    }

    // The salt text includes rebirths "in invariant culture". Integers format
    // the same in every culture .NET ships today, so this is cheap insurance
    // rather than a known hazard: it fails if someone ever swaps the formatting
    // for something culture-sensitive and runs on a machine where it matters.
    [Fact]
    public void TheCurrentCultureDoesNotMoveTheMapping()
    {
        var before = BuddyHatch.Roll("c0ffee00-1234-4abc-8def-0123456789ab", 3);
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            Assert.Equal(before, BuddyHatch.Roll("c0ffee00-1234-4abc-8def-0123456789ab", 3));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void RollingTheSameInputsTwiceGivesAnEqualGenome()
    {
        for (var i = 0; i < 200; i++)
        {
            var uuid = Guid.NewGuid().ToString();
            Assert.Equal(BuddyHatch.Roll(uuid, i % 5), BuddyHatch.Roll(uuid, i % 5));
        }
    }

    [Fact]
    public void ARebirthIsANewRoll()
    {
        // Not a guarantee for any one pair — two rolls can collide — but over
        // a handful of rebirths a buddy that never changes means rebirths is
        // not reaching the hash at all.
        var genomes = Enumerable.Range(0, 8).Select(r => BuddyHatch.Roll("rebirth-probe", r)).Distinct().Count();
        Assert.True(genomes > 1, "every rebirth rolled the same buddy");
    }

    [Fact]
    public void BadInputsAreRefused()
    {
        Assert.Throws<ArgumentNullException>(() => BuddyHatch.Roll(null!, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => BuddyHatch.Roll("x", -1));
    }

    // --- the distribution ------------------------------------------------

    private const int Seeds = 200_000;

    // One sweep, checked several ways: every genome is well-formed, the rarity
    // and shiny rates land within half a percentage point of the table, and
    // every species at every rarity actually comes out. The seeds are
    // deterministic strings rather than Guid.NewGuid(), so a failure here
    // reproduces.
    [Fact]
    public void TwoHundredThousandSeedsMatchTheTableAndAreAllValid()
    {
        var rarity = new int[5];
        var shiny = 0;
        var pairs = new HashSet<(BuddySpecies, BuddyRarity)>();
        var eyes = new HashSet<BuddyEyes>();
        var hats = new HashSet<BuddyHat>();
        var personalities = new HashSet<BuddyPersonality>();
        var peaks = new HashSet<int>();

        for (var i = 0; i < Seeds; i++)
        {
            var g = BuddyHatch.Roll($"seed-{i}", i % 4);
            AssertValid(g);
            rarity[(int)g.Rarity]++;
            if (g.Shiny) shiny++;
            pairs.Add((g.Species, g.Rarity));
            eyes.Add(g.Eyes);
            hats.Add(g.Hat);
            personalities.Add(g.Primary);
            personalities.Add(g.Secondary);
            peaks.Add(Array.IndexOf(StatsOf(g), StatsOf(g).Max()));
        }

        var expected = new[] { 60.0, 25.0, 10.0, 4.0, 1.0 };
        for (var r = 0; r < 5; r++)
        {
            var percent = 100.0 * rarity[r] / Seeds;
            Assert.True(Math.Abs(percent - expected[r]) <= 0.5,
                $"{(BuddyRarity)r}: {percent:F3}% against {expected[r]}%");
        }
        var shinyPercent = 100.0 * shiny / Seeds;
        Assert.True(Math.Abs(shinyPercent - 1.0) <= 0.5, $"shiny: {shinyPercent:F3}%");

        foreach (var s in Enum.GetValues<BuddySpecies>())
            foreach (var r in Enum.GetValues<BuddyRarity>())
                Assert.True(pairs.Contains((s, r)), $"{s} never hatched {r}");
        Assert.Equal(Enum.GetValues<BuddyEyes>().Length, eyes.Count);
        Assert.Equal(Enum.GetValues<BuddyHat>().Length, hats.Count);
        Assert.Equal(Enum.GetValues<BuddyPersonality>().Length, personalities.Count);
        Assert.Equal(5, peaks.Count);
    }

    private static int[] StatsOf(BuddyGenome g) =>
        new[] { g.Stats.Debugging, g.Stats.Patience, g.Stats.Chaos, g.Stats.Wisdom, g.Stats.Snark };

    private static void AssertValid(BuddyGenome g)
    {
        Assert.True(Enum.IsDefined(g.Species));
        Assert.Equal(BuddyTaxonomy.FamilyOf(g.Species), g.Family);
        Assert.True(Enum.IsDefined(g.Rarity));
        Assert.True(Enum.IsDefined(g.Eyes));
        Assert.True(Enum.IsDefined(g.Hat));
        Assert.Equal(g.Rarity == BuddyRarity.Common, g.Hat == BuddyHat.None);
        Assert.NotEqual(g.Primary, g.Secondary);
        Assert.False(string.IsNullOrWhiteSpace(g.Name));
        Assert.True(char.IsUpper(g.Name[0]));

        // Exactly one peak (75+), exactly one dump (floor..floor+15), and the
        // rest strictly between — so "peak" and "dump" are never ambiguous.
        var floor = BuddyTaxonomy.StatFloor(g.Rarity);
        var stats = StatsOf(g);
        Assert.All(stats, v => Assert.InRange(v, floor, 100));
        Assert.Single(stats, v => v >= BuddyHatch.PeakMin);
        Assert.Single(stats, v => v < floor + BuddyHatch.DumpSpan);
        Assert.Equal(3, stats.Count(v => v >= floor + BuddyHatch.DumpSpan && v <= BuddyHatch.OrdinaryMax));
    }

    // --- the pieces ------------------------------------------------------

    [Theory]
    [InlineData(0, "Common")]
    [InlineData(5_999, "Common")]
    [InlineData(6_000, "Uncommon")]
    [InlineData(8_499, "Uncommon")]
    [InlineData(8_500, "Rare")]
    [InlineData(9_499, "Rare")]
    [InlineData(9_500, "Epic")]
    [InlineData(9_899, "Epic")]
    [InlineData(9_900, "Legendary")]
    [InlineData(9_999, "Legendary")]
    public void RarityCutoffsSitWhereTheTableSays(int basisPoints, string expected)
    {
        Assert.Equal(Enum.Parse<BuddyRarity>(expected), BuddyHatch.RarityFrom(basisPoints));
    }

    // The counts are pinned in BuddyHatch so appending to an enum does not
    // re-scale existing rolls. This fails the day they drift, which is when
    // someone has to choose: leave the new member unreachable in v1, or
    // start v2.
    [Fact]
    public void HatchCountsMatchTheEnums()
    {
        Assert.Equal(BuddyHatch.SpeciesCount, Enum.GetValues<BuddySpecies>().Length);
        Assert.Equal(BuddyHatch.EyesCount, Enum.GetValues<BuddyEyes>().Length);
        Assert.Equal(BuddyHatch.HatCount, Enum.GetValues<BuddyHat>().Length - 1);
        Assert.Equal(BuddyHatch.StatCount, Enum.GetValues<BuddyStat>().Length);
        Assert.Equal(BuddyHatch.PersonalityCount, Enum.GetValues<BuddyPersonality>().Length);
        Assert.Equal(16, BuddyHatch.FirstSyllables.Length);
        Assert.Equal(16, BuddyHatch.SecondSyllables.Length);
    }
}

public class BuddyTaxonomyTests
{
    [Fact]
    public void EverySpeciesHasAFamilyInThePlansGrouping()
    {
        var byFamily = Enum.GetValues<BuddySpecies>()
            .GroupBy(BuddyTaxonomy.FamilyOf)
            .ToDictionary(g => g.Key, g => g.ToArray());

        Assert.Equal(new[] { BuddySpecies.Duck, BuddySpecies.Goose, BuddySpecies.Owl, BuddySpecies.Penguin }, byFamily[BuddyFamily.Birds]);
        Assert.Equal(new[] { BuddySpecies.Cat, BuddySpecies.Rabbit, BuddySpecies.Capybara, BuddySpecies.Chonk }, byFamily[BuddyFamily.Critters]);
        Assert.Equal(new[] { BuddySpecies.Octopus, BuddySpecies.Axolotl, BuddySpecies.Turtle, BuddySpecies.Snail }, byFamily[BuddyFamily.Aquatic]);
        Assert.Equal(new[] { BuddySpecies.Dragon, BuddySpecies.Ghost }, byFamily[BuddyFamily.Mythic]);
        Assert.Equal(new[] { BuddySpecies.Cactus, BuddySpecies.Mushroom }, byFamily[BuddyFamily.Flora]);
        Assert.Equal(new[] { BuddySpecies.Robot, BuddySpecies.Blob }, byFamily[BuddyFamily.Constructs]);
    }

    [Theory]
    [InlineData("Common", 5)]
    [InlineData("Uncommon", 15)]
    [InlineData("Rare", 25)]
    [InlineData("Epic", 35)]
    [InlineData("Legendary", 50)]
    public void StatFloorsAreThePlansNumbers(string rarity, int floor)
    {
        Assert.Equal(floor, BuddyTaxonomy.StatFloor(Enum.Parse<BuddyRarity>(rarity)));
    }

    // A value outside the enum is what an appended-but-unmapped member looks
    // like to these switches; it has to throw, not fall into some other row.
    [Fact]
    public void ValuesOutsideTheTablesThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BuddyTaxonomy.FamilyOf((BuddySpecies)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => BuddyTaxonomy.StatFloor((BuddyRarity)99));
    }
}

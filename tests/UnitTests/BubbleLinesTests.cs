using Xunit;

namespace HatchAI.Tests;

// The buddy's lines. The failure worth guarding is an empty or missing pool —
// a moment added to BuddyMoment with no row here throws on the first bubble,
// in a timer, on a user's machine — and a rotation that repeats itself before
// it has used what it has.
public class BubbleLinesTests
{
    private static BuddyGenome Genome(BuddyPersonality primary, BuddyPersonality secondary, BuddyRarity rarity) =>
        BuddyHatch.Roll("lines", 0) with { Primary = primary, Secondary = secondary, Rarity = rarity };

    // Ints rather than the enums themselves: they are internal, and a public
    // theory cannot take them as parameters.
    public static IEnumerable<object[]> EveryCombination() =>
        from m in Enum.GetValues<BuddyMoment>()
        from p in Enum.GetValues<BuddyPersonality>()
        from r in Enum.GetValues<BuddyRarity>()
        select new object[] { (int)m, (int)p, (int)r };

    [Theory]
    [MemberData(nameof(EveryCombination))]
    public void EveryMomentHasLinesForEveryone(int m, int p, int r)
    {
        var (moment, personality, rarity) = ((BuddyMoment)m, (BuddyPersonality)p, (BuddyRarity)r);
        var pool = BubbleLines.PoolFor(moment, personality, rarity);

        Assert.Equal(rarity >= BuddyRarity.Rare ? 4 : 3, pool.Count);
        Assert.Equal(pool.Count, pool.Distinct().Count());
        Assert.All(pool, line =>
        {
            Assert.False(string.IsNullOrWhiteSpace(line));
            // The bubble is small; a line that needs three rows reads as a
            // paragraph from a desktop pet.
            Assert.True(line.Length <= 40, $"too long for a bubble: \"{line}\"");
        });
    }

    [Theory]
    [MemberData(nameof(EveryCombination))]
    public void ARotationSaysEveryLineOnceBeforeRepeating(int m, int p, int r)
    {
        var (moment, personality, rarity) = ((BuddyMoment)m, (BuddyPersonality)p, (BuddyRarity)r);
        var secondary = (BuddyPersonality)(((int)personality + 3) % 8);
        var genome = Genome(personality, secondary, rarity);
        var pool = BubbleLines.PoolFor(moment, personality, rarity);

        for (var start = 0; start < pool.Count; start++)
        {
            var said = Enumerable.Range(start, pool.Count).Select(d => BubbleLines.Pick(moment, genome, d)).ToList();
            Assert.Equal(pool.OrderBy(l => l), said.OrderBy(l => l));
        }
        Assert.Equal(BubbleLines.Pick(moment, genome, 0), BubbleLines.Pick(moment, genome, pool.Count));
    }

    [Fact]
    public void ADrawNamesExactlyOneLine()
    {
        // Secondary Cheerful is offset 0, so draw n is the pool's n-th line.
        var g = Genome(BuddyPersonality.Grumpy, BuddyPersonality.Cheerful, BuddyRarity.Common);
        Assert.Equal("Done. You're welcome.", BubbleLines.Pick(BuddyMoment.Responded, g, 0));
        Assert.Equal("There. Happy now?", BubbleLines.Pick(BuddyMoment.Responded, g, 1));

        var legend = Genome(BuddyPersonality.Zen, BuddyPersonality.Cheerful, BuddyRarity.Legendary);
        Assert.Equal("A legend grows stronger!", BubbleLines.Pick(BuddyMoment.Evolved, legend, 3));
    }

    [Fact]
    public void TheSecondaryPersonalityShiftsWhereTheRotationStarts()
    {
        var a = Genome(BuddyPersonality.Cheerful, BuddyPersonality.Grumpy, BuddyRarity.Common);
        var b = Genome(BuddyPersonality.Cheerful, BuddyPersonality.Sleepy, BuddyRarity.Common);
        Assert.NotEqual(BubbleLines.Pick(BuddyMoment.SessionStarted, a, 0), BubbleLines.Pick(BuddyMoment.SessionStarted, b, 0));
    }

    // The caller's counter can wrap or start anywhere; neither end of int may
    // throw or land outside the pool.
    [Theory]
    [InlineData(-1)]
    [InlineData(-7)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void ExtremeDrawsStayInThePool(int draw)
    {
        var g = Genome(BuddyPersonality.Chaotic, BuddyPersonality.Zen, BuddyRarity.Epic);
        var pool = BubbleLines.PoolFor(BuddyMoment.LongIdle, BuddyPersonality.Chaotic, BuddyRarity.Epic);
        Assert.Contains(BubbleLines.Pick(BuddyMoment.LongIdle, g, draw), pool);
    }
}

using Xunit;

namespace HatchAI.Tests;

// CB-202 voice mix: the description table. Every enum value must have one, so
// appending a personality or species without writing its voice fails here
// rather than quietly sending the table fallback for that buddy forever.
public class BubbleVoiceProfilesTests
{
    public static IEnumerable<object[]> Personalities() =>
        Enum.GetValues<BuddyPersonality>().Select(p => new object[] { (int)p });

    public static IEnumerable<object[]> Species() =>
        Enum.GetValues<BuddySpecies>().Select(s => new object[] { (int)s });

    [Theory]
    [MemberData(nameof(Personalities))]
    public void EveryPersonalityHasAVoice(int p) => AssertSendable(BubbleVoiceProfiles.Of((BuddyPersonality)p));

    [Theory]
    [MemberData(nameof(Species))]
    public void EverySpeciesHasAVoice(int s) => AssertSendable(BubbleVoiceProfiles.Of((BuddySpecies)s));

    [Fact]
    public void NoTwoVoicesAreTheSame()
    {
        var all = Enum.GetValues<BuddyPersonality>().Select(BubbleVoiceProfiles.Of)
            .Concat(Enum.GetValues<BuddySpecies>().Select(BubbleVoiceProfiles.Of))
            .ToList();

        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Fact]
    public void AnUnknownValueThrowsRatherThanSendingNothing()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BubbleVoiceProfiles.Of((BuddyPersonality)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => BubbleVoiceProfiles.Of((BuddySpecies)99));
    }

    // Short, plain ASCII, and none of the characters cmd.exe would re-parse
    // (the rule BubblePrompt.System keeps), nor the parentheses the prompt
    // wraps each description in.
    private static void AssertSendable(string voice)
    {
        Assert.False(string.IsNullOrWhiteSpace(voice));
        Assert.Equal(voice.Trim(), voice);
        Assert.True(voice.Split(' ').Length <= 12, voice);
        Assert.All(voice, c => Assert.InRange(c, ' ', '~'));
        foreach (var c in "&|<>^%\"()")
            Assert.DoesNotContain(c, voice);
    }
}

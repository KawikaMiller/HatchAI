using Xunit;

namespace HatchAI.Tests;

// The model's answer is untrusted data; these are the doors it has to get
// through. One case per rule, plus the near-misses that must still pass.
public class BubbleLineValidatorTests
{
    [Theory]
    [InlineData("Tests are green, nicely done.")]
    [InlineData("  Tests are green.  ")]
    [InlineData("Hm. Interesting.")]
    [InlineData("Wait — what just happened…")]
    [InlineData("Oh… well.")]
    [InlineData("Ab")]
    [InlineData("Don’t panic.")]
    [InlineData("Sixty-four bits of pure joy!")]
    public void AcceptsOrdinaryLines(string raw) =>
        Assert.Equal(raw.Trim(), BubbleLineValidator.Validate(raw));

    [Theory]
    [InlineData("\"Nice work.\"", "Nice work.")]
    [InlineData("'Nice work.'", "Nice work.")]
    [InlineData("“Nice work.”", "Nice work.")]
    [InlineData("‘Nice work.’", "Nice work.")]
    [InlineData("  \"Nice work.\"  ", "Nice work.")]
    [InlineData("\"Nice \"work\" today.\"", "Nice \"work\" today.")]
    public void RemovesOneSurroundingPairOfQuotes(string raw, string expected) =>
        Assert.Equal(expected, BubbleLineValidator.Validate(raw));

    [Fact]
    public void OnlyOnePairIsRemoved() =>
        Assert.Equal("\"Nice work.\"", BubbleLineValidator.Validate("\"\"Nice work.\"\""));

    [Fact]
    public void MismatchedQuotesStay() =>
        Assert.Equal("\"Nice work.", BubbleLineValidator.Validate("\"Nice work."));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    [InlineData("\"a\"")]
    [InlineData("a")]
    public void RejectsEmptyAndTooShort(string? raw) => Assert.Null(BubbleLineValidator.Validate(raw));

    [Fact]
    public void LengthBoundsAreInclusive()
    {
        var hundred = new string('a', 100);
        Assert.Equal(hundred, BubbleLineValidator.Validate(hundred));
        Assert.Null(BubbleLineValidator.Validate(hundred + "a"));
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("... !!!")]
    [InlineData("— —")]
    public void NeedsALetter(string raw) => Assert.Null(BubbleLineValidator.Validate(raw));

    [Theory]
    [InlineData("first line\nsecond line")]
    [InlineData("first line\rsecond")]
    [InlineData("tab\there")]
    [InlineData("bell\u0007here")]
    [InlineData("del\u007Fhere")]
    [InlineData("c1\u0085here")]
    public void RejectsControlsAndNewlines(string raw) => Assert.Null(BubbleLineValidator.Validate(raw));

    [Theory]
    [InlineData("use `code` here")]
    [InlineData("so **bold** now")]
    [InlineData("so __bold__ now")]
    [InlineData("# heading text")]
    [InlineData("- a list item")]
    [InlineData("* a list item")]
    [InlineData("see [this](thing) now")]
    public void RejectsMarkdown(string raw) => Assert.Null(BubbleLineValidator.Validate(raw));

    [Theory]
    [InlineData("a - b is fine")]
    [InlineData("a * b is fine")]
    [InlineData("under_score is fine")]
    [InlineData("issue #5 is fine")]
    [InlineData("a [bracket] is fine")]
    public void MarkdownNearMissesPass(string raw) => Assert.Equal(raw, BubbleLineValidator.Validate(raw));

    [Theory]
    [InlineData("nice ‮evil")]
    [InlineData("nice ‪evil")]
    [InlineData("nice ⁦evil")]
    [InlineData("nice ⁩evil")]
    [InlineData("family ‍ joined")]
    [InlineData("heart ️ selector")]
    [InlineData("sunny ☀ day")]
    [InlineData("check ✔ mark")]
    [InlineData("star ⭐ shape")]
    [InlineData("face \U0001F600 grin")]
    [InlineData("card \U0001F0CF joker")]
    public void RejectsEmojiAndBidi(string raw) => Assert.Null(BubbleLineValidator.Validate(raw));

    [Theory]
    [InlineData("A range end ◿ is fine")]
    [InlineData("dash – and dash — fine")]
    [InlineData("accents café fine")]
    [InlineData("just below ◿ fine")]
    public void CharactersNextToTheBannedBlocksPass(string raw) =>
        Assert.Equal(raw, BubbleLineValidator.Validate(raw));

    [Theory]
    [InlineData("One. Two.", true)]
    [InlineData("One! Two? Yes", false)]
    [InlineData("One... Two... Three", false)]
    [InlineData("One… Two", true)]
    [InlineData("One?! Two", true)]
    [InlineData("Just one sentence...", true)]
    [InlineData("One.Two.Three.", true)]
    [InlineData("One. Two. Three.", false)]
    [InlineData("Evolved? ...cool. Zzz.", false)]
    public void AtMostTwoSentences(string raw, bool passes) =>
        Assert.Equal(passes, BubbleLineValidator.Validate(raw) is not null);

    [Theory]
    [InlineData("As an AI I have no view")]
    [InlineData("I am a language model")]
    [InlineData("I can't help with that")]
    [InlineData("I can’t help with that")]
    [InlineData("I cannot say")]
    [InlineData("I'm sorry about that")]
    [InlineData("I’m sorry about that")]
    [InlineData("I am sorry about that")]
    [InlineData("I won't do it")]
    [InlineData("My system prompt says hi")]
    [InlineData("Following instructions here")]
    [InlineData("Claude says hello")]
    [InlineData("ANTHROPIC built me")]
    [InlineData("Request: something")]
    [InlineData("You typed [redacted] again")]
    [InlineData("Mail [email] now")]
    [InlineData("go to http://x")]
    [InlineData("go to www.x.com")]
    [InlineData("ping @someone now")]
    public void RejectsRefusalsAndMeta(string raw) => Assert.Null(BubbleLineValidator.Validate(raw));

    [Fact]
    public void RefusalChecksIgnoreCase() => Assert.Null(BubbleLineValidator.Validate("I CANNOT say"));

    [Fact]
    public void ARefusalWordInsideAnotherWordIsStillRefused() =>
        // "instruction" is matched as a substring on purpose: "instructions",
        // "instructional" and "Instruction:" all read as the model talking
        // about its own brief.
        Assert.Null(BubbleLineValidator.Validate("Instructional videos are fun"));

    // The table's lines never pass through the validator in production; they
    // are what is shown when the validator says no. Run over all of them once
    // anyway, as a check that the validator's rules are not stricter than the
    // voice the table was written in. Measured at feature/bubble-e1: exactly
    // the lines below fail, all for the reason given, and the table was left
    // alone.
    //
    //   Evolved? ...cool. Zzz.   three sentences (the planner's prediction)
    //   A new one? Okay. Okay!   three sentences
    //   Okay! Okay! Going!       three sentences
    //
    // Any other line failing (or this one ceasing to) makes this test fail
    // and print the difference.
    private static readonly string[] KnownTableFailures =
    {
        "A new one? Okay. Okay!",
        "Evolved? ...cool. Zzz.",
        "Okay! Okay! Going!",
    };

    [Fact]
    public void ValidatorOverTheWholeTable()
    {
        var failures = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var moment in Enum.GetValues<BuddyMoment>())
            foreach (var personality in Enum.GetValues<BuddyPersonality>())
                foreach (var rarity in Enum.GetValues<BuddyRarity>())
                    foreach (var line in BubbleLines.PoolFor(moment, personality, rarity))
                        if (BubbleLineValidator.Validate(line) is null)
                            failures.Add($"{line}");

        Assert.Equal(KnownTableFailures, failures.ToArray());
    }
}

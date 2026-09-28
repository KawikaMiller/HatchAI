using Xunit;

namespace HatchAI.Tests;

// What is masked before a prompt may leave the machine. Every secret here is
// assembled at run time from harmless pieces so this file itself carries
// nothing a secret scanner would flag; every one is invented.
public class BubblePromptRedactorTests
{
    private static string Redact(string text, string? home = null) => BubblePromptRedactor.Redact(text, home);

    private static string A(int n, char c = 'q') => new(c, n);

    [Theory]
    [MemberData(nameof(Secrets))]
    public void MasksSecretShapes(string secret)
    {
        var result = Redact($"please use {secret} in the deploy script");
        Assert.Equal("please use [redacted] in the deploy script", result);
        Assert.DoesNotContain(secret, result);
    }

    public static IEnumerable<object[]> Secrets() => new[]
    {
        "sk-" + "ant-api03-" + A(30),
        "sk-" + A(20, 'Q'),
        "ghp_" + A(36, 'x'), "gho_" + A(36, 'x'), "ghu_" + A(36, 'x'), "ghs_" + A(36, 'x'), "ghr_" + A(36, 'x'),
        "github_pat_" + A(30, 'Z') + "_" + A(20, 'Y'),
        "glpat-" + A(20, 'k'),
        "xoxb-" + "1234567890-" + A(10),
        "xoxa-" + A(12), "xoxp-" + A(12), "xoxr-" + A(12), "xoxs-" + A(12),
        "AKIA" + A(16, 'B'), "ASIA" + A(16, 'C'),
        "AIza" + A(35, 'd'),
        "npm_" + A(36, 'e'),
        "eyJhbGciOiJIUzI1NiJ9." + "eyJzdWIiOiIxMjMifQ." + "c2ln",
        "eyJhbGciOiJIUzI1NiJ9." + "eyJzdWIiOiIxMjMifQ.",
        A(32, 'f'), A(64, '9'), "0123456789abcdef0123456789ABCDEF0123",
        "QWxhZGRpbjpvcGVuIHNlc2FtZQ" + "0123456789ABCDEFGHIJKLMN",
        "abc_DEF-ghi_JKL-mno_PQR-stu_VWX-yz01-23456",
    }.Select(s => new object[] { s });

    [Theory]
    [InlineData("Authorization: Bearer abcdefghijklmnop1234", "[redacted]")]
    [InlineData("Authorization:\nBearer abcdefghijklmnop1234", "[redacted]")]
    [InlineData("header is bearer abcdefghijklmnop", "header is [redacted]")]
    [InlineData("Basic dXNlcjpwYXNzd29yZA== sent", "[redacted] sent")]
    [InlineData("token=abcdefghijklmnopqrst", "[redacted]")]
    public void MasksBearerBasicAndToken(string input, string expected) =>
        Assert.Equal(expected, Redact(input + " and more words").Replace(" and more words", ""));

    [Fact]
    public void MasksPrivateKeyBlocks()
    {
        var block = "-----BEGIN RSA PRIVATE KEY-----\nMIIabc\ndef\n-----END RSA PRIVATE KEY-----";
        Assert.Equal("my key [redacted] is broken", Redact($"my key {block} is broken"));
    }

    [Fact]
    public void MasksAPrivateKeyBlockThatNeverEnds() =>
        Assert.Equal("my key [redacted]", Redact("my key -----BEGIN PRIVATE KEY-----\nMIIabc\ndef"));

    [Fact]
    public void MasksCredentialsInUrls() =>
        Assert.Equal("connect to postgres://[redacted]@db.internal:5432/app please",
            Redact("connect to postgres://admin:hunter2@db.internal:5432/app please"));

    [Fact]
    public void AUrlWithoutCredentialsStays() =>
        Assert.Equal("open https://example.org/docs please", Redact("open https://example.org/docs please"));

    [Theory]
    [InlineData("API_KEY=abc123")]
    [InlineData("export DB_PASSWORD=hunter2")]
    [InlineData("SECRET_TOKEN = \"a b c\"")]
    [InlineData("db.passwd='x y'")]
    [InlineData("MY_PWD=x")]
    [InlineData("AWS_CREDENTIAL=x")]
    [InlineData("BASIC_AUTH=x")]
    [InlineData("MY_COOKIE=x")]
    [InlineData("SESSION_ID=x")]
    [InlineData("api_key=x")]
    public void MasksEnvStyleSecrets(string assignment)
    {
        var result = Redact($"the env has {assignment} set");
        Assert.DoesNotContain("hunter2", result);
        Assert.DoesNotContain("abc123", result);
        Assert.Contains("[redacted]", result);
        Assert.EndsWith("set", result);
    }

    [Fact]
    public void EnvMaskingKeepsWhatFollowsTheValue() =>
        Assert.Equal("then [redacted] then run", Redact("then API_KEY=abc123 then run"));

    [Fact]
    public void ATaskKeyIsRedactedByDesign() =>
        // The rule looks at the *name*: "task_key" contains KEY, so its value
        // goes. Over-masking is the intended direction of error.
        Assert.Equal("set [redacted] now", Redact("set task_key=5 now"));

    [Theory]
    [InlineData("if a == b then stop the run")]
    [InlineData("set name=value and keep going")]
    public void NonSecretAssignmentsStay(string text) => Assert.Equal(text, Redact(text));

    [Fact]
    public void MasksEmails() =>
        Assert.Equal("mail [email] and [email] about it", Redact("mail a.b+c@example.co.uk and x@y.io about it"));

    [Fact]
    public void ReplacesTheHomeDirectoryBothWays()
    {
        Assert.Equal("edit ~/src/app/main.cs now", Redact("edit C:\\Users\\bob/src/app/main.cs now", "C:\\Users\\bob"));
        Assert.Equal("edit ~\\src\\app now", Redact("edit C:\\Users\\bob\\src\\app now", "C:/Users/bob"));
        Assert.Equal("edit ~/src now", Redact("edit /home/bob/src now", "/home/bob/"));
    }

    [Fact]
    public void HomeDirectoryDoesNotEatALongerName() =>
        Assert.Equal("see /home/bobby/src and ~/src", Redact("see /home/bobby/src and /home/bob/src", "/home/bob"));

    [Fact]
    public void HomeDirectoryIsCaseInsensitiveOnWindowsOnly()
    {
        var result = Redact("edit c:\\users\\BOB\\x now", "C:\\Users\\bob");
        Assert.Equal(OperatingSystem.IsWindows() ? "edit ~\\x now" : "edit c:\\users\\BOB\\x now", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    public void NoUsableHomeSkipsOnlyThatStep(string? home) =>
        Assert.Equal("edit /x/y now and [email]", Redact("edit /x/y now and me@site.org", home));

    // --- the planner's list, pinned where a boundary could loosen it ---

    [Fact]
    public void ShortestAnthropicKeyIsMasked() =>
        Assert.Equal("use [redacted] now", Redact("use sk-ant-" + A(10) + " now"));

    [Fact]
    public void ShapesHaveNoBoundaryAfterThem() =>
        // The planner's AKIA pattern ends at sixteen characters wherever it
        // is; a lookahead here would let AKIA + 17 slip through.
        Assert.Equal("id [redacted]Z tail", Redact("id AKIA" + A(16, 'B') + "Z tail"));

    [Theory]
    [InlineData("my password: hunter2 ok", "my [redacted] ok")]
    [InlineData("the session:\n  abc here", "the [redacted] here")]
    [InlineData("MY_SECRET =\n'quoted\nvalue' end", "[redacted] end")]
    public void AssignmentsTakeColonsAndLineBreaks(string input, string expected) =>
        Assert.Equal(expected, Redact(input));

    // --- near misses that must survive ---

    [Fact]
    public void ThirtyOneHexCharactersStay()
    {
        var text = "the id " + A(31, 'a') + " is short";
        Assert.Equal(text, Redact(text));
    }

    [Fact]
    public void FortyHexCharactersInsideAWordStayHexButAreCaughtAsMixed()
    {
        // 32+ hex glued to letters fails the hex rule's boundary, but at 40+
        // with a digit the base64 rule still takes it.
        var glued = A(20, 'a') + "1" + A(19, 'b') + "zz";
        Assert.Equal("id [redacted] end", Redact($"id {glued} end"));
    }

    [Fact]
    public void TokenFollowedByAnOrdinaryWordStays() =>
        Assert.Equal("the token refresh is slow today", Redact("the token refresh is slow today"));

    [Fact]
    public void ALongWordWithNoDigitIsNotBase64() =>
        Assert.Equal("say " + A(45, 'x') + " twice", Redact("say " + A(45, 'x') + " twice"));

    [Fact]
    public void ALongRunOfDigitsIsNotBase64AndTooShortForHexBoundaryLogic()
    {
        // Digits are also hex, so 40 of them go by the hex rule; 31 do not.
        Assert.Equal("n " + A(31, '7') + " ok", Redact("n " + A(31, '7') + " ok"));
        Assert.Equal("n [redacted] ok", Redact("n " + A(40, '7') + " ok"));
    }

    [Fact]
    public void AMixedRunOfThirtyNineStays()
    {
        var run = A(38, 'q') + "1";
        Assert.Equal("v " + run + " ok", Redact("v " + run + " ok"));
    }

    [Fact]
    public void ShortKeyLookalikesStay()
    {
        Assert.Equal("the sk-short prefix is fine", Redact("the sk-short prefix is fine"));
        // No boundary before "sk-": the planner's pattern has none, so a word ending in "sk" is redacted too.
        Assert.Equal("a ri[redacted] is over", Redact("a risk-" + A(20) + " is over"));
        Assert.Equal("ghp_short and AKIA123 stay", Redact("ghp_short and AKIA123 stay"));
    }

    // --- the clean-up pass ---

    [Fact]
    public void CollapsesWhitespaceAndStripsControlAndBidiCharacters() =>
        Assert.Equal("one two three four", Redact("  one\n\n two\t\tthree\u0007 \u202Efour\u2066  "));

    [Fact]
    public void EmptyAndNullInputAreEmpty()
    {
        Assert.Equal("", Redact(""));
        Assert.Equal("", BubblePromptRedactor.Redact(null!, null));
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("12345 !!!")]
    [InlineData("[redacted]")]
    [InlineData("[redacted] [email] hi")]
    [InlineData("\n\t  ")]
    public void FewerThanThreeLettersOutsideMarkersIsNothing(string text) => Assert.Equal("", Redact(text));

    [Fact]
    public void ExactlyThreeLettersIsEnough() => Assert.Equal("abc", Redact("abc"));

    [Fact]
    public void AnUnicodeWordCountsAsLetters() => Assert.Equal("\u00E9\u00E9\u00E9", Redact("\u00E9\u00E9\u00E9"));

    // --- length ---

    [Fact]
    public void TwoHundredCharactersPassUntouched()
    {
        var text = A(200);
        Assert.Equal(text, Redact(text));
    }

    [Fact]
    public void LongTextIsCutAtAWordBoundaryFromOneFiftyOn()
    {
        var text = string.Join(" ", Enumerable.Repeat("word", 100));
        var result = Redact(text);

        Assert.EndsWith("\u2026", result);
        Assert.True(result.Length <= 200);
        Assert.True(result.Length > 150);
        Assert.Equal("word", result.TrimEnd('\u2026').Split(' ').Last());
    }

    [Fact]
    public void WithoutAWordBoundaryFromOneFiftyOnItCutsHard()
    {
        var text = "short " + A(400);
        var result = Redact(text);
        Assert.Equal(("short " + A(400))[..200] + "\u2026", result);
    }

    [Fact]
    public void ASpaceBeforeOneFiftyIsNotABoundary()
    {
        var text = A(100) + " " + A(400, 'r');
        Assert.Equal(text[..200] + "\u2026", Redact(text));
    }

    [Fact]
    public void ACutNeverSplitsASurrogatePair()
    {
        var text = A(199) + "\U0001F600" + A(50);
        var result = Redact(text);
        Assert.Equal(A(199) + "\u2026", result);
    }

    [Fact]
    public void ACutNeverLeavesAHalfMarkerBehind()
    {
        // 190 letters, then a secret whose marker would straddle the cut.
        var text = A(193, 'q') + " " + "AKIA" + A(16, 'B') + " tail tail tail";
        var result = Redact(text);
        Assert.DoesNotContain("[", result);
        Assert.EndsWith("\u2026", result);

        var email = A(195, 'q') + " " + "me@example.org tail tail tail";
        var result2 = Redact(email);
        Assert.DoesNotContain("[", result2);
    }

    [Fact]
    public void AWholeMarkerBeforeTheCutIsKept()
    {
        var text = "AKIA" + A(16, 'B') + " " + A(300, 'q');
        Assert.StartsWith("[redacted] ", Redact(text));
    }

    [Fact]
    public void AnUnrelatedBracketAtTheCutIsKept()
    {
        var text = A(197) + "[ab" + A(50);
        var result = Redact(text);
        Assert.Contains("[ab", result);
    }

    [Fact]
    public void OnlyTheFirstTwoThousandCharactersAreRead()
    {
        var secretAfterWindow = A(2000, 'z') + " AKIA" + A(16, 'B');
        Assert.DoesNotContain("AKIA", Redact(secretAfterWindow));

        // ...and the secret inside the window is masked before the window's
        // own tail is cut, not after: it cannot be sliced in half and escape.
        var straddling = A(1980, 'z') + " AKIA" + A(16, 'B');
        Assert.DoesNotContain("AKIABBBB", Redact(straddling));
    }

    [Fact]
    public void TheWindowNeverSplitsASurrogatePair()
    {
        var text = A(1999) + "\U0001F600" + "tail";
        var result = Redact(text);
        Assert.DoesNotContain('\uD83D', result);
    }
}

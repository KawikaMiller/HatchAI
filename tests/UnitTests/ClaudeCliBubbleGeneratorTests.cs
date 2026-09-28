using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace HatchAI.Tests;

// The buddy's headless `claude -p` as a value, and the generator's failure arms
// (CB-202). Nothing here spawns a process: the invocation is asserted on the
// ProcessStartInfo, and the generator is driven through its seams. The process
// itself is tests/IntegrationTests' ClaudeCliBubbleProcessTests, against a fake
// CLI.
//
// Every flag is pinned because each one is the only thing standing between a
// bubble and a measured cost: a transcript the ledger counts, an orb for the
// buddy talking to itself, or thinking that turned 29 output tokens into 3,367.
// A flag dropped in a refactor would fail nowhere else — the call still works,
// it just costs more, or feeds the buddy its own voice.
public class ClaudeCliBubbleGeneratorTests : IDisposable
{
    private readonly string _workDir =
        Path.Combine(Path.GetTempPath(), "cb-bubblegen-" + Guid.NewGuid().ToString("N"), BubbleVoice.WorkDirLeaf);

    public void Dispose()
    {
        var root = Path.GetDirectoryName(_workDir)!;
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private static BubbleRequest Request(string? transcript = null, string? prompt = null) => new(
        BuddyMoment.SessionStarted, BuddyPersonality.Cheerful, BuddyPersonality.Sassy, BuddyRarity.Rare, BuddySpecies.Owl,
        new BuddyStats(50, 50, 50, 50, 50), "Claude-Buddy", prompt, transcript);

    // ---- the invocation ---------------------------------------------------

    [Fact]
    public void TheArgumentsAreExactlyThePlannedInvocation()
    {
        Assert.Equal(
            new[]
            {
                "-p", "--model", "haiku", "--output-format", "json", "--no-session-persistence",
                "--tools", "", "--strict-mcp-config", "--disable-slash-commands",
                "--settings", "{\"disableAllHooks\":true,\"alwaysThinkingEnabled\":false}",
                "--system-prompt", "the voice",
            },
            ClaudeCliBubbleGenerator.Arguments("the voice"));
    }

    // Asserted as parsed JSON as well as as a string, so the pin says what it
    // means: thinking off (the ~160x cost difference measured on the plan) and
    // hooks off (the orb and the SessionStarted feedback loop), and nothing else
    // smuggled into the user's effective settings for this call.
    [Fact]
    public void TheSettingsTurnThinkingOffAndHooksOffAndNothingElse()
    {
        using var doc = JsonDocument.Parse(ClaudeCliBubbleGenerator.Settings);
        var props = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);

        Assert.Equal(2, props.Count);
        Assert.Equal(JsonValueKind.False, props["alwaysThinkingEnabled"].ValueKind);
        Assert.Equal(JsonValueKind.True, props["disableAllHooks"].ValueKind);
    }

    [Fact]
    public void TheStartInfoRunsTheLocatedBinaryInTheWorkDirWithNoWindow()
    {
        var info = ClaudeCliBubbleGenerator.StartInfoFor("/opt/claude", _workDir, "the voice");

        Assert.Equal("/opt/claude", info.FileName);
        Assert.Equal(_workDir, info.WorkingDirectory);
        Assert.True(info.RedirectStandardInput);
        Assert.True(info.RedirectStandardOutput);
        Assert.True(info.RedirectStandardError);
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.Equal(ClaudeCliBubbleGenerator.Arguments("the voice"), info.ArgumentList);

        // The joined string is left empty: ArgumentList is what quotes the
        // JSON and the empty --tools value, and setting both is an error.
        Assert.Equal(string.Empty, info.Arguments);
    }

    // A BOM on stdin would reach the model as the first character of the user
    // message, and one on stdout would sit in front of the JSON.
    [Fact]
    public void EveryStreamIsUtf8WithoutAByteOrderMark()
    {
        var info = ClaudeCliBubbleGenerator.StartInfoFor("/opt/claude", _workDir, "the voice");

        foreach (var encoding in new[] { info.StandardInputEncoding, info.StandardOutputEncoding, info.StandardErrorEncoding })
        {
            Assert.NotNull(encoding);
            Assert.Equal(Encoding.UTF8.WebName, encoding!.WebName);
            Assert.Empty(encoding.GetPreamble());
        }
    }

    // The work dir under temp does not exist on a fresh machine, and a missing
    // WorkingDirectory fails the start with an error that says nothing useful.
    [Fact]
    public void TheWorkDirIsCreatedIfItIsMissing()
    {
        Assert.False(Directory.Exists(_workDir));

        ClaudeCliBubbleGenerator.StartInfoFor("/opt/claude", _workDir, "the voice");

        Assert.True(Directory.Exists(_workDir));
    }

    // ---- IsOwnWorkDir -----------------------------------------------------

    [Theory]
    // The folder itself, as a cwd, on either platform, with or without a
    // trailing separator, in any case.
    [InlineData(@"C:\Users\someone\AppData\Local\Temp\claudebuddy-bubble-voice", true)]
    [InlineData(@"C:\Users\someone\AppData\Local\Temp\claudebuddy-bubble-voice\", true)]
    [InlineData("/var/folders/xy/T/claudebuddy-bubble-voice", true)]
    [InlineData("/var/folders/xy/T/claudebuddy-bubble-voice//", true)]
    [InlineData("/tmp/ClaudeBuddy-Bubble-Voice", true)]
    [InlineData("claudebuddy-bubble-voice", true)]
    // Claude Code's project folder names for it: the real Windows shape (every
    // non-alphanumeric turned to '-', measured), the Unix shape, and the shape
    // TranscriptReader.EncodeCwd produces on Windows, which keeps the colon.
    [InlineData("C--Users-someone-AppData-Local-Temp-claudebuddy-bubble-voice", true)]
    [InlineData("-var-folders-xy-T-claudebuddy-bubble-voice", true)]
    [InlineData(@"-C:-Users-someone-AppData-Local-Temp-claudebuddy-bubble-voice", true)]
    [InlineData("C--USERS-SOMEONE-TEMP-CLAUDEBUDDY-BUBBLE-VOICE", true)]
    // Not ours: temp itself (where summaries used to run, and where plenty of
    // other things do), something inside the folder, a name that merely
    // contains the leaf, one glued to it without a dash, and nothing at all.
    [InlineData(@"C:\Users\someone\AppData\Local\Temp", false)]
    [InlineData("C--Users-someone-AppData-Local-Temp", false)]
    [InlineData("/tmp/claudebuddy-bubble-voice/child", false)]
    [InlineData("C--repo-claudebuddy-bubble-voice-notes", false)]
    [InlineData("C--repomyclaudebuddy-bubble-voice", false)]
    [InlineData("claudebuddy-bubble", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void IsOwnWorkDirRecognisesTheVoicesFolderAndNothingElse(string? path, bool expected)
    {
        Assert.Equal(expected, BubbleVoice.IsOwnWorkDir(path));
    }

    // The two things the scanner and the status scan actually see: the real
    // WorkDir as a cwd, and Claude Code's project-folder name for it. Claude
    // Buddy checked the second through its own TranscriptReader.EncodeCwd,
    // which HatchAI did not port; this spells out Claude Code's own rule
    // instead (every character that is not a letter or digit becomes '-',
    // per IsOwnWorkDir's comment), which is the name the ledger really meets.
    [Fact]
    public void TheRealWorkDirAndItsEncodingAreBothRecognised()
    {
        Assert.True(BubbleVoice.IsOwnWorkDir(BubbleVoice.WorkDir));
        var projectFolder = System.Text.RegularExpressions.Regex.Replace(BubbleVoice.WorkDir, "[^A-Za-z0-9]", "-");
        Assert.True(BubbleVoice.IsOwnWorkDir(projectFolder));
    }

    // ---- trimming the prompt ------------------------------------------------

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(" \n\t ", null)]
    [InlineData("  fix   the\n\nbuild  ", "fix the build")]
    public void TrimCollapsesWhitespaceAndAnswersNullForNothing(string? text, string? expected)
    {
        Assert.Equal(expected, ClaudeCliBubbleGenerator.Trim(text));
    }

    [Fact]
    public void APromptAtTheLimitIsKeptWhole()
    {
        var text = new string('a', ClaudeCliBubbleGenerator.MaxPromptChars);
        Assert.Equal(text, ClaudeCliBubbleGenerator.Trim(text));
    }

    [Fact]
    public void ALongerPromptIsCutAtTheLimitAndMarked()
    {
        var trimmed = ClaudeCliBubbleGenerator.Trim(new string('a', 500))!;

        Assert.Equal(new string('a', ClaudeCliBubbleGenerator.MaxPromptChars) + "…", trimmed);
    }

    // An emoji straddling the limit: cutting between its halves would send an
    // invalid string. The whole pair goes instead.
    [Fact]
    public void TheCutNeverSplitsASurrogatePair()
    {
        var text = new string('a', ClaudeCliBubbleGenerator.MaxPromptChars - 1) + "\U0001F600" + "tail";

        var trimmed = ClaudeCliBubbleGenerator.Trim(text)!;

        Assert.Equal(new string('a', ClaudeCliBubbleGenerator.MaxPromptChars - 1) + "…", trimmed);
    }

    // ---- the generator's failure arms ---------------------------------------

    // No CLI is the Codex- or Grok-only user's everyday case, not an error:
    // null at once, and nothing read or spawned on the way.
    [Fact]
    public async Task NoCliAnswersNullWithoutReadingOrSpawningAnything()
    {
        var read = false;
        var ran = false;
        var generator = new ClaudeCliBubbleGenerator(
            locate: () => null,
            run: (_, _, _) => { ran = true; return Task.FromResult<string?>("{}"); },
            transcriptLines: _ => { read = true; return Array.Empty<string>(); });

        Assert.Null(await generator.GenerateAsync(Request("/some/transcript.jsonl"), CancellationToken.None));
        Assert.False(read);
        Assert.False(ran);
    }

    // Already stale when asked: nothing is located, read or spawned.
    [Fact]
    public async Task AnAlreadyCancelledRequestDoesNothingAndAnswersNull()
    {
        var located = false;
        var generator = new ClaudeCliBubbleGenerator(
            locate: () => { located = true; return "/opt/claude"; },
            run: (_, _, _) => Task.FromResult<string?>("{}"));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Null(await generator.GenerateAsync(Request(), cts.Token));
        Assert.False(located);
    }

    // Each seam throwing, in turn. The contract is null, never an exception —
    // the controller has one fallback arm, and a throw on the UI thread would
    // take the buddy with it.
    [Fact]
    public async Task ALocateThatThrowsAnswersNull()
    {
        var generator = new ClaudeCliBubbleGenerator(locate: () => throw new IOException("disk"));

        Assert.Null(await generator.GenerateAsync(Request(), CancellationToken.None));
    }

    [Fact]
    public async Task ATranscriptReadThatThrowsAnswersNullAndSpawnsNothing()
    {
        var ran = false;
        var generator = new ClaudeCliBubbleGenerator(
            locate: () => "/opt/claude",
            run: (_, _, _) => { ran = true; return Task.FromResult<string?>("{}"); },
            transcriptLines: _ => throw new UnauthorizedAccessException("nope"));

        Assert.Null(await generator.GenerateAsync(Request("/some/transcript.jsonl"), CancellationToken.None));
        Assert.False(ran);
    }

    [Fact]
    public async Task ARunThatThrowsAnswersNull()
    {
        var generator = new ClaudeCliBubbleGenerator(
            locate: () => "/opt/claude",
            run: (_, _, _) => throw new InvalidOperationException("spawn"),
            transcriptLines: _ => Array.Empty<string>());

        Assert.Null(await generator.GenerateAsync(Request(prompt: "hi"), CancellationToken.None));
    }

    // The production default for `locate` is the uncached lookup, so a CLI
    // installed after launch is noticed without a restart. Asserted by
    // behaviour: the default generator, cancelled, still answers null without
    // touching anything — and ClaudeBinary.Locate, not Path, is what it names.
    [Fact]
    public async Task TheDefaultGeneratorIsConstructibleAndRespectsCancellation()
    {
        var generator = new ClaudeCliBubbleGenerator();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Null(await generator.GenerateAsync(Request(), cts.Token));
    }

    // An injected clock is accepted and does not change the contract. Nothing
    // in the generator reads it today (the timeout is the controller's), which
    // is why the default clock's body is the one line of this class no test
    // reaches.
    [Fact]
    public async Task AnInjectedClockIsAcceptedAndTheFailureArmsStillAnswerNull()
    {
        var generator = new ClaudeCliBubbleGenerator(locate: () => null, clock: () => DateTimeOffset.UnixEpoch);

        Assert.Null(await generator.GenerateAsync(Request(), CancellationToken.None));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("/home/someone", "/home/someone")]
    public void AnEmptyHomeIsNotAHomePrefix(string? home, string? expected)
    {
        Assert.Equal(expected, ClaudeCliBubbleGenerator.HomeForRedaction(home));
    }

    // RunProcessAsync's outermost promise, at its earliest point: a start info
    // the Process will not even accept is null, not an exception.
    [Fact]
    public async Task RunProcessAsyncAnswersNullForAStartInfoItCannotUse()
    {
        Assert.Null(await ClaudeCliBubbleGenerator.RunProcessAsync(null!, "hi", CancellationToken.None));
    }

    // Kill is called from inside a catch that promises null, so it may not
    // throw for a process that has already gone — or was never started.
    [Fact]
    public void KillToleratesAProcessThatWasNeverStarted()
    {
        using var proc = new Process();
        ClaudeCliBubbleGenerator.Kill(proc);
    }
}

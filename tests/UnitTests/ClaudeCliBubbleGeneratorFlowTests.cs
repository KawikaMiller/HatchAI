using System.Diagnostics;
using Xunit;

namespace HatchAI.Tests;

// GenerateAsync's success path, through its seams (CB-202): what is read, what
// is sent, and what comes back. Nothing is spawned — `run` is a fake that
// records the start info and the stdin it was handed.
//
// These assert against the text layer's own functions (BubblePrompt.User,
// BubblePromptRedactor, LatestUserPrompt, BubbleCliOutput, BubbleLineValidator)
// rather than restating their rules, because those rules are E1's and are
// pinned case by case in E1's own tests. What is E2's, and what these pin, is
// the plumbing between them: the transcript read goes through the seam, the
// prompt is redacted before it is trimmed and trimmed before it is sent, only
// BubblePrompt.User's text reaches stdin, none of it reaches the command line,
// and the answer is parsed and validated before it is returned.
public class ClaudeCliBubbleGeneratorFlowTests
{
    private const string TranscriptPath = "/fake/projects/C--repo/session.jsonl";

    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static readonly string[] Lines =
    {
        "{\"type\":\"user\",\"message\":{\"content\":\"an older prompt\"}}",
        "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"ASSISTANT-REPLY-NOT-SENT\"}]}}",
        "{\"type\":\"user\",\"message\":{\"content\":\"please fix the flaky build\"}}",
        "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"ASSISTANT-REPLY-NOT-SENT again\"}]}}",
    };

    private const string Answer = "{\"type\":\"result\",\"is_error\":false,\"result\":\"That build is toast, let us fix it.\"}";

    private static BubbleRequest Request(string? transcript = TranscriptPath, string? prompt = null) => new(
        BuddyMoment.SessionStarted, BuddyPersonality.Cheerful, BuddyPersonality.Sassy, BuddyRarity.Rare, BuddySpecies.Owl,
        new BuddyStats(50, 50, 50, 50, 50), "Claude-Buddy", prompt, transcript);

    private sealed class Recorder
    {
        public ProcessStartInfo? StartInfo;
        public string? Stdin;
        public string? ReadPath;
        public int Reads;

        public ClaudeCliBubbleGenerator Generator(string? stdout = Answer, IEnumerable<string>? lines = null,
            Action? duringRun = null) => new(
            locate: () => "/opt/claude",
            run: (info, stdin, _) =>
            {
                StartInfo = info;
                Stdin = stdin;
                duringRun?.Invoke();
                return Task.FromResult(stdout);
            },
            transcriptLines: path =>
            {
                ReadPath = path;
                Reads++;
                return lines ?? Lines;
            });
    }

    private static string? ExpectedPrompt(IEnumerable<string> lines)
    {
        var raw = LatestUserPrompt.From(lines, SessionSource.ClaudeCode);
        return raw is null ? null : ClaudeCliBubbleGenerator.Trim(BubblePromptRedactor.Redact(raw, Home));
    }

    [Fact]
    public async Task TheLatestPromptIsReadRedactedTrimmedAndSentOverStdinOnly()
    {
        var recorder = new Recorder();

        var line = await recorder.Generator().GenerateAsync(Request(), CancellationToken.None);

        Assert.Equal(BubbleLineValidator.Validate(BubbleCliOutput.Parse(Answer)), line);
        Assert.Equal(TranscriptPath, recorder.ReadPath);

        var expected = Request() with { Prompt = ExpectedPrompt(Lines) };
        Assert.Equal(BubblePrompt.User(expected), recorder.Stdin);

        // What must never be sent: the assistant's words and the transcript's
        // location. And the per-bubble text is on stdin, not the command line.
        Assert.DoesNotContain("ASSISTANT-REPLY-NOT-SENT", recorder.Stdin);
        Assert.DoesNotContain(TranscriptPath, recorder.Stdin);
        Assert.DoesNotContain(recorder.StartInfo!.ArgumentList, a => a.Contains("flaky build"));

        Assert.Equal("/opt/claude", recorder.StartInfo.FileName);
        Assert.Equal(BubbleVoice.WorkDir, recorder.StartInfo.WorkingDirectory);
        Assert.Equal(ClaudeCliBubbleGenerator.Arguments(BubblePrompt.System), recorder.StartInfo.ArgumentList);
    }

    // A home-directory path and a prompt longer than the budget: the home
    // prefix is masked first, then the result is cut, so the mask is never the
    // part that gets cut in half.
    [Fact]
    public async Task ALongPromptWithAHomePathIsRedactedBeforeItIsTrimmed()
    {
        var typed = "look at " + Path.Combine(Home, "secret-project", "notes.txt") + " " + new string('x', 400);
        var lines = new[] { "{\"type\":\"user\",\"message\":{\"content\":" + System.Text.Json.JsonSerializer.Serialize(typed) + "}}" };
        var recorder = new Recorder();

        await recorder.Generator(lines: lines).GenerateAsync(Request(), CancellationToken.None);

        var expected = Request() with { Prompt = ExpectedPrompt(lines) };
        Assert.Equal(BubblePrompt.User(expected), recorder.Stdin);
        Assert.DoesNotContain(Home, recorder.Stdin);
        Assert.True(expected.Prompt!.Length <= ClaudeCliBubbleGenerator.MaxPromptChars + 1);
    }

    // A prompt the caller already filled is used as given, and the transcript
    // is not opened.
    [Fact]
    public async Task APromptAlreadyOnTheRequestIsSentWithoutReadingTheTranscript()
    {
        var recorder = new Recorder();
        var request = Request(prompt: "already redacted");

        await recorder.Generator().GenerateAsync(request, CancellationToken.None);

        Assert.Equal(0, recorder.Reads);
        Assert.Equal(BubblePrompt.User(request), recorder.Stdin);
    }

    // No transcript, or a tail with no user turn in it: the bubble still goes
    // out, without a prompt.
    [Fact]
    public async Task NoTranscriptMeansNoPromptAndNoRead()
    {
        var recorder = new Recorder();

        var line = await recorder.Generator().GenerateAsync(Request(transcript: null), CancellationToken.None);

        Assert.NotNull(line);
        Assert.Equal(0, recorder.Reads);
        Assert.Equal(BubblePrompt.User(Request(transcript: null)), recorder.Stdin);
    }

    [Fact]
    public async Task ATailWithNoUserTurnSendsNoPrompt()
    {
        var recorder = new Recorder();
        var lines = new[] { Lines[1] };

        await recorder.Generator(lines: lines).GenerateAsync(Request(), CancellationToken.None);

        Assert.Equal(BubblePrompt.User(Request() with { Prompt = null }), recorder.Stdin);
    }

    // The failures after the process: no stdout, and output the parser or the
    // validator refuses. Each is "use the table".
    [Theory]
    [InlineData(null)]
    [InlineData("this is not json")]
    [InlineData("{\"type\":\"result\",\"is_error\":true,\"result\":\"oops\"}")]
    [InlineData("{\"type\":\"result\",\"is_error\":false,\"result\":\"\"}")]
    public async Task AnythingButAValidAnswerIsNull(string? stdout)
    {
        var line = await new Recorder().Generator(stdout: stdout).GenerateAsync(Request(), CancellationToken.None);

        Assert.Null(line);
    }

    // Cancelled while the process ran, and it answered anyway: the answer is
    // for a moment the controller has given up on, and is dropped.
    [Fact]
    public async Task AnAnswerThatArrivesAfterCancellationIsDropped()
    {
        using var cts = new CancellationTokenSource();
        var recorder = new Recorder();

        var line = await recorder.Generator(duringRun: cts.Cancel).GenerateAsync(Request(), cts.Token);

        Assert.NotNull(recorder.Stdin);
        Assert.Null(line);
    }
}

using System.Diagnostics;
using Xunit;

namespace HatchAI.Tests;

// Why the generator answered null, as the bubble log sees it (CB-202, after
// live use). Each failure arm driven through the seams that already exist,
// and the one field read back. Nothing spawns a process: a failed start and
// a non-zero exit are reported by RunProcessAsync, which the integration
// suite drives against a fake CLI (ClaudeCliBubbleDiagnosticsProcessTests).
public class ClaudeCliBubbleGeneratorDiagnosticsTests
{
    private const string Good = "{\"type\":\"result\",\"is_error\":false,\"result\":\"Nice, that build is green.\"}";

    private static BubbleRequest Request() => new(
        BuddyMoment.Responded, BuddyPersonality.Cheerful, BuddyPersonality.Sassy, BuddyRarity.Rare, BuddySpecies.Owl,
        new BuddyStats(50, 50, 50, 50, 50), "Claude-Buddy", "already redacted");

    private static ClaudeCliBubbleGenerator WithStdout(string? stdout) => new(
        locate: () => "/opt/claude",
        run: (_, _, _) => Task.FromResult(stdout),
        transcriptLines: _ => Array.Empty<string>());

    [Fact]
    public void NothingHasFailedBeforeTheFirstCall()
    {
        Assert.Null(new ClaudeCliBubbleGenerator(locate: () => null).LastFailure);
    }

    [Fact]
    public async Task NoCli()
    {
        var generator = new ClaudeCliBubbleGenerator(locate: () => null);

        Assert.Null(await generator.GenerateAsync(Request(), CancellationToken.None));
        Assert.Equal(BubbleFailure.NoCli, generator.LastFailure);
    }

    // An injected seam answering null says nothing about why, so the
    // generator does not guess between a start failure and an exit code.
    [Fact]
    public async Task ARunSeamThatAnswersNullIsAProcessFailure()
    {
        var generator = WithStdout(null);

        Assert.Null(await generator.GenerateAsync(Request(), CancellationToken.None));
        Assert.Equal(BubbleFailure.ProcessFailed, generator.LastFailure);
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("{\"type\":\"result\",\"is_error\":true,\"result\":\"oops\"}")]
    public async Task OutputTheParserRefusesIsBadOutput(string stdout)
    {
        var generator = WithStdout(stdout);

        Assert.Null(await generator.GenerateAsync(Request(), CancellationToken.None));
        Assert.Equal(BubbleFailure.BadOutput, generator.LastFailure);
    }

    // Parsed fine, and three sentences long: the validator's to refuse.
    [Fact]
    public async Task ALineTheValidatorRefusesIsRejectedByValidator()
    {
        const string stdout =
            "{\"type\":\"result\",\"is_error\":false,\"result\":\"One. Two. Three. Four.\"}";
        Assert.NotNull(BubbleCliOutput.Parse(stdout));
        Assert.Null(BubbleLineValidator.Validate(BubbleCliOutput.Parse(stdout)));

        var generator = WithStdout(stdout);

        Assert.Null(await generator.GenerateAsync(Request(), CancellationToken.None));
        Assert.Equal(BubbleFailure.RejectedByValidator, generator.LastFailure);
    }

    [Fact]
    public async Task ASeamThatThrowsIsThrew()
    {
        var generator = new ClaudeCliBubbleGenerator(locate: () => throw new IOException("disk"));

        Assert.Null(await generator.GenerateAsync(Request(), CancellationToken.None));
        Assert.Equal(BubbleFailure.Threw, generator.LastFailure);
    }

    [Fact]
    public async Task AnAlreadyCancelledCallIsCancelled()
    {
        var generator = WithStdout(Good);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Null(await generator.GenerateAsync(Request(), cts.Token));
        Assert.Equal(BubbleFailure.Cancelled, generator.LastFailure);
    }

    // Cancelled while the process ran, and it answered anyway.
    [Fact]
    public async Task CancelledDuringTheRunIsCancelledEvenWithAnAnswer()
    {
        using var cts = new CancellationTokenSource();
        var generator = new ClaudeCliBubbleGenerator(
            locate: () => "/opt/claude",
            run: (_, _, _) => { cts.Cancel(); return Task.FromResult<string?>(Good); });

        Assert.Null(await generator.GenerateAsync(Request(), cts.Token));
        Assert.Equal(BubbleFailure.Cancelled, generator.LastFailure);
    }

    // Reset at the start of every call, so a success after a failure does
    // not keep reporting the old one.
    [Fact]
    public async Task ASuccessClearsTheLastFailure()
    {
        string? stdout = null;
        var generator = new ClaudeCliBubbleGenerator(
            locate: () => "/opt/claude",
            run: (_, _, _) => Task.FromResult(stdout));

        await generator.GenerateAsync(Request(), CancellationToken.None);
        Assert.Equal(BubbleFailure.ProcessFailed, generator.LastFailure);

        stdout = Good;
        Assert.NotNull(await generator.GenerateAsync(Request(), CancellationToken.None));
        Assert.Null(generator.LastFailure);
    }

    // RunProcessAsync's report, for the one failure reachable without a
    // process at all: a start info it cannot use is a failed start.
    [Fact]
    public async Task RunProcessAsyncReportsAStartItCouldNotMake()
    {
        var reported = new List<BubbleFailure>();

        Assert.Null(await ClaudeCliBubbleGenerator.RunProcessAsync(
            new ProcessStartInfo(Path.Combine(Path.GetTempPath(), "no-such-claude-" + Guid.NewGuid().ToString("N")))
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
            "hi", CancellationToken.None, reported.Add));

        Assert.Equal(new[] { BubbleFailure.StartFailed }, reported);
    }

    // Cancellation is the caller's to know, so it is not reported.
    [Fact]
    public async Task RunProcessAsyncReportsNothingWhenAlreadyCancelled()
    {
        var reported = new List<BubbleFailure>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Null(await ClaudeCliBubbleGenerator.RunProcessAsync(
            new ProcessStartInfo("/opt/claude"), "hi", cts.Token, reported.Add));

        Assert.Empty(reported);
    }

    // The production `run` seam is RunProcessAsync with the report wired: a
    // binary that is not there reaches the generator as StartFailed, not the
    // generic ProcessFailed an injected seam gets.
    [Fact]
    public async Task TheDefaultRunSeamCarriesTheStartFailureThrough()
    {
        var missing = Path.Combine(Path.GetTempPath(), "no-such-claude-" + Guid.NewGuid().ToString("N"));
        var generator = new ClaudeCliBubbleGenerator(locate: () => missing, transcriptLines: _ => Array.Empty<string>());

        Assert.Null(await generator.GenerateAsync(Request(), CancellationToken.None));
        Assert.Equal(BubbleFailure.StartFailed, generator.LastFailure);
    }
}

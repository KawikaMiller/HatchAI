using System.Text;
using System.Text.Json;
using Xunit;

namespace HatchAI.Tests;

// The whole generator against real files and a real subprocess (CB-202): a
// transcript on disk read through the production TranscriptReader.TailLines,
// the production RunProcessAsync, and a fake `claude` that records what it
// was sent. The one thing not production is the binary, so this is the test
// that says only the approved fields leave the machine *as a process sees
// them*, not as a seam was handed them.
public class ClaudeCliBubbleEndToEndTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cb-bubble-e2e-" + Guid.NewGuid().ToString("N"));

    public ClaudeCliBubbleEndToEndTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string FakeClaude()
    {
        const string json = "{\"type\":\"result\",\"is_error\":false,\"result\":\"Go get that build.\"}";
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(_dir, "claude.cmd");
            File.WriteAllText(path,
                "@echo off\r\necho %* > \"%~dp0args.txt\"\r\nfindstr \"^\" > \"%~dp0stdin.txt\"\r\necho " + json + "\r\nexit /b 0\r\n",
                new UTF8Encoding(false));
            return path;
        }
        else
        {
            var path = Path.Combine(_dir, "claude");
            File.WriteAllText(path,
                "#!/bin/sh\ndir=$(dirname \"$0\")\nprintf '%s\\n' \"$@\" > \"$dir/args.txt\"\ncat > \"$dir/stdin.txt\"\necho '" + json + "'\nexit 0\n",
                new UTF8Encoding(false));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return path;
        }
    }

    [Fact]
    public async Task OnlyTheApprovedFieldsReachTheProcessAndOnlyOverStdin()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var typed = "fix " + Path.Combine(home, "work", "app.cs") + " before lunch";
        var transcript = Path.Combine(_dir, "session.jsonl");
        File.WriteAllLines(transcript, new[]
        {
            "{\"type\":\"user\",\"message\":{\"content\":" + JsonSerializer.Serialize(typed) + "}}",
            "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"ASSISTANT-REPLY-NOT-SENT\"}]}}",
        });

        var fake = FakeClaude();
        var generator = new ClaudeCliBubbleGenerator(locate: () => fake);
        var request = new BubbleRequest(
            BuddyMoment.SessionStarted, BuddyPersonality.Zen, BuddyPersonality.Grumpy, BuddyRarity.Epic, BuddySpecies.Cactus,
            new BuddyStats(91, 12, 64, 37, 78), "my-project", null, transcript);

        var line = await generator.GenerateAsync(request, CancellationToken.None);

        Assert.Equal("Go get that build.", line);

        var expectedPrompt = ClaudeCliBubbleGenerator.Trim(BubblePromptRedactor.Redact(typed, home));
        var expected = BubblePrompt.User(request with { Prompt = expectedPrompt });
        var stdin = File.ReadAllText(Path.Combine(_dir, "stdin.txt"));
        // Line endings normalised: findstr, the Windows fake's stdin recorder,
        // may rewrite them, and they are not what this asserts.
        Assert.Equal(
            expected.Replace("\r\n", "\n").TrimEnd('\n', ' '),
            stdin.Replace("\r\n", "\n").TrimEnd('\n', ' '));
        // The stats reach the process, all five and in their fixed order
        // (distinct values, so a dropped or swapped one would show).
        Assert.Contains("debugging 91, patience 12, chaos 64, wisdom 37, snark 78.", stdin);
        Assert.DoesNotContain("ASSISTANT-REPLY-NOT-SENT", stdin);
        Assert.DoesNotContain(transcript, stdin);

        var args = File.ReadAllText(Path.Combine(_dir, "args.txt"));
        Assert.DoesNotContain("before lunch", args);
        Assert.DoesNotContain("my-project", args);
        Assert.DoesNotContain("snark 78", args);
    }
}

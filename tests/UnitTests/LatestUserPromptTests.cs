using System.Text.Json;
using Xunit;

namespace HatchAI.Tests;

// The row shapes are the ones ChatTranscript/CodexTranscript's own fixtures
// use, which were taken from real transcripts; the text in them is synthetic.
// (This machine's real transcripts were not read for these.) LatestUserPrompt
// restates none of the mappers' noise rules, so each case here is a noise rule
// *running*, not being reimplemented.
public class LatestUserPromptTests
{
    private static int _n;

    private static string User(string text, string extra = "") =>
        "{\"parentUuid\":null,\"isSidechain\":false," + extra + "\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":"
        + JsonSerializer.Serialize(text) + "},\"uuid\":\"u-" + Interlocked.Increment(ref _n)
        + "\",\"timestamp\":\"2026-09-26T20:00:00.000Z\"}";

    private const string Reply =
        """{"parentUuid":"u","isSidechain":false,"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Done."}]},"uuid":"a1","timestamp":"2026-09-26T20:00:05.000Z"}""";

    private const string ToolResultOnly =
        """{"parentUuid":"a1","isSidechain":false,"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_01","type":"tool_result","content":"file contents"}]},"uuid":"u2","timestamp":"2026-09-26T20:00:06.000Z"}""";

    private static string? Claude(params string[] lines) => LatestUserPrompt.From(lines, SessionSource.ClaudeCode);

    [Fact]
    public void TakesTheLastUserTurn() =>
        Assert.Equal("second ask", Claude(User("first ask"), Reply, User("second ask"), Reply));

    [Fact]
    public void ASystemReminderIsNotAPrompt() =>
        Assert.Equal("real ask", Claude(User("real ask"), User("<system-reminder>be careful</system-reminder>")));

    [Fact]
    public void AMetaRowIsSkipped() =>
        Assert.Equal("real ask", Claude(User("real ask"), User("hook output", "\"isMeta\":true,")));

    [Fact]
    public void ASidechainRowIsSkipped() =>
        Assert.Equal("real ask", Claude(User("real ask"), User("subagent chatter").Replace("\"isSidechain\":false", "\"isSidechain\":true")));

    [Fact]
    public void AToolResultOnlyRowIsSkipped() =>
        Assert.Equal("real ask", Claude(User("real ask"), Reply, ToolResultOnly));

    [Fact]
    public void ACommandRowIsSkipped() =>
        Assert.Equal("real ask", Claude(User("real ask"),
            User("<command-name>/clear</command-name>\n<command-message>clear</command-message>")));

    [Fact]
    public void AQueuedPromptCounts() =>
        Assert.Equal("queued words", Claude(User("earlier"), Reply,
            """{"type":"queue-operation","operation":"enqueue","timestamp":"2026-09-26T20:01:00.000Z","sessionId":"s","content":"queued words"}"""));

    [Fact]
    public void APictureWithNoCaptionIsSkipped() =>
        Assert.Equal("real ask", Claude(User("real ask"),
            """{"isSidechain":false,"type":"user","message":{"role":"user","content":[{"type":"image","source":{"type":"base64","media_type":"image/png","data":"iVBORw0KGgo="}}]},"uuid":"u3","timestamp":"2026-09-26T20:02:00.000Z"}"""));

    [Fact]
    public void AWindowWithNoUserRowIsNull()
    {
        Assert.Null(Claude(Reply, ToolResultOnly));
        Assert.Null(Claude());
        Assert.Null(Claude("", "not json", "{broken"));
    }

    private static string CodexUser(int ordinal, string text) =>
        "{\"timestamp\":\"2026-08-19T16:57:08.663Z\",\"ordinal\":" + ordinal
        + ",\"type\":\"event_msg\",\"payload\":{\"type\":\"item_completed\",\"item\":{\"type\":\"UserMessage\",\"id\":\"x\",\"content\":[{\"type\":\"text\",\"text\":"
        + JsonSerializer.Serialize(text) + ",\"text_elements\":[]}]}}}";

    private const string CodexReply =
        """{"timestamp":"2026-08-19T16:57:09.000Z","ordinal":10,"type":"event_msg","payload":{"type":"item_completed","item":{"type":"AgentMessage","content":[{"type":"Text","text":"On it."}]}}}""";

    [Fact]
    public void ReadsACodexRollout() =>
        Assert.Equal("port this to codex",
            LatestUserPrompt.From(new[] { CodexUser(1, "first"), CodexReply, CodexUser(11, "port this to codex"), CodexReply }, SessionSource.Codex));

    [Fact]
    public void CodexContextInjectionIsNotAPrompt() =>
        Assert.Equal("real",
            LatestUserPrompt.From(new[] { CodexUser(1, "real"), CodexUser(2, "<environment_context>\n  <cwd>/x</cwd>\n</environment_context>") }, SessionSource.Codex));

    [Fact]
    public void CodexWithNoUserRowIsNull() =>
        Assert.Null(LatestUserPrompt.From(new[] { CodexReply }, SessionSource.Codex));

    [Theory]
    [InlineData(SessionSource.Grok)]
    [InlineData(SessionSource.OpenClaw)]
    public void SourcesWithoutALocalTranscriptAreNull(SessionSource source) =>
        Assert.Null(LatestUserPrompt.From(new[] { User("would be found for claude") }, source));
}

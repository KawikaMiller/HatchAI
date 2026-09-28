using Xunit;

namespace HatchAI.Tests;

// The CLI's --output-format json stdout. The fixtures are synthetic but shaped
// like the planner's recorded run (exit 0; result, usage, modelUsage) — no
// live call was made for these tests.
public class BubbleCliOutputTests
{
    private const string Ok =
        """{"type":"result","subtype":"success","is_error":false,"duration_ms":2712,"num_turns":1,"result":"Another green build, how ordinary.","stop_reason":"end_turn","session_id":"00000000-0000-0000-0000-000000000000","total_cost_usd":0.00064,"usage":{"input_tokens":560,"output_tokens":24},"modelUsage":{"claude-haiku-4-5-20251001":{"inputTokens":560,"outputTokens":24}}}""";

    [Fact]
    public void ReadsTheResult() =>
        Assert.Equal("Another green build, how ordinary.", BubbleCliOutput.Parse(Ok));

    [Fact]
    public void ToleratesSurroundingWhitespace() =>
        Assert.Equal("Another green build, how ordinary.", BubbleCliOutput.Parse("\n" + Ok + "\n"));

    [Fact]
    public void AnErrorResultIsNothing() =>
        Assert.Null(BubbleCliOutput.Parse(Ok.Replace("\"is_error\":false", "\"is_error\":true")));

    [Fact]
    public void ANonBooleanErrorFlagIsNothing() =>
        Assert.Null(BubbleCliOutput.Parse(Ok.Replace("\"is_error\":false", "\"is_error\":\"no\"")));

    [Fact]
    public void AMissingErrorFlagIsTolerated() =>
        Assert.Equal("hi there", BubbleCliOutput.Parse("""{"result":"hi there"}"""));

    [Theory]
    [InlineData("""{"is_error":false}""")]
    [InlineData("""{"is_error":false,"result":null}""")]
    [InlineData("""{"is_error":false,"result":42}""")]
    [InlineData("""{"is_error":false,"result":""}""")]
    [InlineData("""{"is_error":false,"result":"   "}""")]
    [InlineData("[1,2]")]
    [InlineData("\"result\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"result\":\"cut off")]
    public void AnythingElseIsNothing(string stdout) =>
        Assert.Null(BubbleCliOutput.Parse(stdout));

    [Fact]
    public void ReadsTheResultEventOfAnEventArray()
    {
        var events = "[{\"type\":\"system\",\"subtype\":\"init\"},{\"type\":\"assistant\"},7," + Ok + "]";
        Assert.Equal("Another green build, how ordinary.", BubbleCliOutput.Parse(events));
    }

    [Fact]
    public void AnEventArrayWithoutAResultEventIsNothing() =>
        Assert.Null(BubbleCliOutput.Parse("""[{"type":"system"},{"type":5},{"type":"assistant"}]"""));
}

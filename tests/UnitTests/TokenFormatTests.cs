using System.Globalization;
using Xunit;

namespace HatchAI.Tests;

// The card's numbers. The boundaries are where a formatter goes wrong — the
// step from 999 to 1k, from 9.99k to 10k, from 999k to 1M — and truncation is
// the rule that matters most: an egg at 2,499 tokens must never read as
// "2.5k", because the card prints it beside "Hatches at 2.5k" — and likewise
// a Third at 42,499 beside "Rebirth available at 42.5k" (CB-195).
public class TokenFormatTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(950, "950")]
    [InlineData(999, "999")]
    [InlineData(1_000, "1k")]
    [InlineData(1_234, "1.23k")]
    [InlineData(1_500, "1.5k")]
    [InlineData(2_499, "2.49k")]
    [InlineData(2_500, "2.5k")]
    [InlineData(9_999, "9.99k")]
    [InlineData(10_000, "10k")]
    [InlineData(12_345, "12.3k")]
    [InlineData(12_500, "12.5k")]
    [InlineData(42_499, "42.4k")]
    [InlineData(42_500, "42.5k")]
    [InlineData(99_999, "99.9k")]
    [InlineData(100_000, "100k")]
    [InlineData(999_999, "999k")]
    [InlineData(1_000_000, "1M")]
    [InlineData(1_234_567, "1.23M")]
    [InlineData(999_999_999, "999M")]
    [InlineData(1_000_000_000, "1B")]
    [InlineData(1_000_000_000_000, "1T")]
    [InlineData(1_234_000_000_000_000, "1234T")]
    [InlineData(long.MaxValue, "9223372T")]
    [InlineData(-1_500, "-1.5k")]
    [InlineData(long.MinValue, "-9223372T")]
    public void ThreeSignificantDigitsTruncated(long tokens, string expected)
    {
        Assert.Equal(expected, TokenFormat.Compact(tokens));
    }

    [Fact]
    public void AGermanMachineStillGetsAPoint()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("1.23M", TokenFormat.Compact(1_234_567));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}

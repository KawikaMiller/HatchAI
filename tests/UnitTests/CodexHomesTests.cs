using System.IO;
using Xunit;

namespace HatchAI.Tests;

// CodexUsageAccounts.Homes, the one method HatchAI kept from Claude Buddy's
// CodexUsage.cs. Claude Buddy's own case, verbatim; the rate-limit parsing
// cases went with the parser.
public class CodexHomesTests
{
    [Fact]
    public void HomesAlwaysIncludesTheDefaultAndSkipsBlanksAndDuplicates()
    {
        var homes = CodexUsageAccounts.Homes("/Users/w", new[] { " ", ".codex", "work", "/abs/.codex-abs", "" });
        Assert.Equal(new[]
        {
            Path.Combine("/Users/w", ".codex"),
            Path.Combine("/Users/w", "work"),
            "/abs/.codex-abs",
        }, homes);
    }
}
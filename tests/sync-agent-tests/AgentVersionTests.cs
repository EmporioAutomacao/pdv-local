using SyncAgent.Update;

namespace SyncAgent.Tests;

public class AgentVersionTests
{
    [Theory]
    [InlineData("1.6.46", "1.6.45", true)]
    [InlineData("1.6.46", "1.6.46", true)]
    [InlineData("1.6.46", "1.6.47", false)]
    [InlineData("1.6.99", "1.6.100", false)]
    [InlineData("1.6.100", "1.6.99", true)]
    public void ComparesNumerically(string current, string candidate, bool expected)
    {
        Assert.Equal(expected, AgentVersion.IsDowngradeOrSame(current, candidate));
    }

    [Theory]
    [InlineData("0.1.0-dev", "9.0.0")]
    [InlineData("", "9.0.0")]
    [InlineData(null, "9.0.0")]
    [InlineData("1.6.46", "nova")]
    [InlineData("1.6.46", null)]
    public void UnparseableVersionFailsClosed(string? current, string? candidate)
    {
        Assert.True(AgentVersion.IsDowngradeOrSame(current, candidate));
    }
}

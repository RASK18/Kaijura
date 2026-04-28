using Kaijura.App.Services;

namespace Kaijura.Tests;

public sealed class JiraClientTests
{
    [Theory]
    [InlineData("jira.example.local", "https://jira.example.local")]
    [InlineData("https://jira.example.local/", "https://jira.example.local")]
    [InlineData("https://jira.example.local/jira/", "https://jira.example.local/jira")]
    public void NormalizeHostKeepsExpectedBaseAddress(string input, string expected)
    {
        Assert.Equal(expected, JiraClient.NormalizeHost(input));
    }
}

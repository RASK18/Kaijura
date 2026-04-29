using Kaijura.App.Services;
using Kaijura.App.Models;
using System.Net;
using System.Net.Http;
using System.Text;

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

    [Fact]
    public async Task GetLatestRelevantCommentParsesCommentsAndSkipsIgnoredAuthors()
    {
        var handler = new FakeHandler("""
            {
              "startAt": 0,
              "maxResults": 100,
              "total": 2,
              "comments": [
                {
                  "id": "50002",
                  "author": {
                    "name": "ci-bot",
                    "key": "ci-bot-key",
                    "displayName": "CI Bot",
                    "emailAddress": "ci@example.local"
                  },
                  "created": "2026-04-28T10:05:00.000+0000",
                  "updated": "2026-04-28T10:05:00.000+0000"
                },
                {
                  "id": "50001",
                  "author": {
                    "name": "alice",
                    "key": "alice-key",
                    "displayName": "Alice",
                    "emailAddress": "alice@example.local"
                  },
                  "created": "2026-04-28T10:00:00.000+0000",
                  "updated": "2026-04-28T10:02:00.000+0000"
                }
              ]
            }
            """);
        var client = new JiraClient(new HttpClient(handler));
        var config = new AppConfig { JiraHost = "https://jira.example.local", UserName = "rafa" };

        var comment = await client.GetLatestRelevantCommentAsync(
            config,
            "pat-token",
            "BTR-1802",
            ["CI Bot"],
            CancellationToken.None);

        Assert.NotNull(comment);
        Assert.Equal("50001", comment.Id);
        Assert.Equal("Alice", comment.AuthorLabel);
        Assert.Equal("Bearer", handler.LastRequest?.Headers.Authorization?.Scheme);
        Assert.Equal("pat-token", handler.LastRequest?.Headers.Authorization?.Parameter);
        Assert.Equal("/rest/api/2/issue/BTR-1802/comment", handler.LastRequest?.RequestUri?.AbsolutePath);
        Assert.Contains("orderBy=-created", handler.LastRequest?.RequestUri?.Query);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly string _json;

        public FakeHandler(string json)
        {
            _json = json;
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json")
            };

            return Task.FromResult(response);
        }
    }
}

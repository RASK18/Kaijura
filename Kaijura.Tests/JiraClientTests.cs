using Kaijura.App.Services;
using Kaijura.App.Models;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

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

    [Fact]
    public async Task GetTransitionsParsesRequiredFields()
    {
        var handler = new FakeHandler("""
            {
              "transitions": [
                {
                  "id": "31",
                  "name": "Resolve",
                  "to": { "name": "Resolved" },
                  "fields": {
                    "comment": {
                      "required": true,
                      "name": "Comment",
                      "operations": ["add"],
                      "schema": { "type": "comment", "system": "comment" }
                    },
                    "worklog": {
                      "required": true,
                      "name": "Log Work",
                      "operations": ["add"],
                      "schema": { "type": "array", "items": "worklog", "system": "worklog" }
                    },
                    "customfield_12345": {
                      "required": true,
                      "name": "Block Comment",
                      "operations": ["set"],
                      "schema": { "type": "string", "custom": "com.atlassian.jira.plugin.system.customfieldtypes:textarea" }
                    },
                    "resolution": {
                      "required": true,
                      "name": "Resolucion",
                      "operations": ["set"],
                      "schema": { "type": "resolution", "system": "resolution" },
                      "allowedValues": [
                        { "id": "10000", "name": "Solucionada" },
                        { "id": "10001", "name": "No solucionada" }
                      ]
                    }
                  }
                }
              ]
            }
            """);
        var client = new JiraClient(new HttpClient(handler));
        var config = new AppConfig { JiraHost = "https://jira.example.local", UserName = "rafa" };

        var transitions = await client.GetTransitionsAsync(config, "pat-token", "BTR-1802", CancellationToken.None);

        var transition = Assert.Single(transitions);
        Assert.Equal("31", transition.Id);
        Assert.Equal("Resolve", transition.Name);
        Assert.Equal("Resolved", transition.ToStatus);
        Assert.Equal("/rest/api/2/issue/BTR-1802/transitions", handler.LastRequest?.RequestUri?.AbsolutePath);
        Assert.Contains("expand=transitions.fields", handler.LastRequest?.RequestUri?.Query);

        var comment = Assert.Single(transition.Fields, field => field.Id == "comment");
        Assert.True(comment.Required);
        Assert.Equal("comment", comment.SchemaSystem);
        Assert.Contains("add", comment.Operations);

        var worklog = Assert.Single(transition.Fields, field => field.Id == "worklog");
        Assert.Equal("worklog", worklog.SchemaItems);

        var textField = Assert.Single(transition.Fields, field => field.Id == "customfield_12345");
        Assert.Equal("Block Comment", textField.Name);
        Assert.Equal("string", textField.SchemaType);

        var resolution = Assert.Single(transition.Fields, field => field.Id == "resolution");
        Assert.Equal("resolution", resolution.SchemaSystem);
        Assert.Collection(
            resolution.AllowedValues,
            value =>
            {
                Assert.Equal("10000", value.Id);
                Assert.Equal("Solucionada", value.Name);
            },
            value =>
            {
                Assert.Equal("10001", value.Id);
                Assert.Equal("No solucionada", value.Name);
            });
    }

    [Fact]
    public async Task TransitionIssueSendsCommentWorklogAndTextFieldPayload()
    {
        var handler = new FakeHandler("{}");
        var client = new JiraClient(new HttpClient(handler));
        var config = new AppConfig { JiraHost = "https://jira.example.local", UserName = "rafa" };
        var started = new DateTimeOffset(2026, 4, 29, 10, 15, 0, TimeSpan.FromHours(2));

        await client.TransitionIssueAsync(
            config,
            "pat-token",
            "10001",
            new JiraTransitionUpdate(
                "31",
                "Ready to resolve",
                "1h 30m",
                "Investigated and fixed",
                started,
                new Dictionary<string, string>
                {
                    ["customfield_12345"] = "Blocked by provider"
                },
                new Dictionary<string, JiraTransitionAllowedValue>
                {
                    ["resolution"] = new("10000", "Solucionada", string.Empty)
                }),
            CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.LastRequest?.Method);
        Assert.Equal("/rest/api/2/issue/10001/transitions", handler.LastRequest?.RequestUri?.AbsolutePath);
        Assert.NotNull(handler.LastRequestBody);

        using var document = JsonDocument.Parse(handler.LastRequestBody!);
        var root = document.RootElement;
        Assert.Equal("31", root.GetProperty("transition").GetProperty("id").GetString());
        Assert.Equal("Blocked by provider", root.GetProperty("fields").GetProperty("customfield_12345").GetString());
        Assert.Equal("10000", root.GetProperty("fields").GetProperty("resolution").GetProperty("id").GetString());

        var comment = root.GetProperty("update").GetProperty("comment").EnumerateArray().Single().GetProperty("add");
        Assert.Equal("Ready to resolve", comment.GetProperty("body").GetString());

        var worklog = root.GetProperty("update").GetProperty("worklog").EnumerateArray().Single().GetProperty("add");
        Assert.Equal("1h 30m", worklog.GetProperty("timeSpent").GetString());
        Assert.Equal("2026-04-29T10:15:00.000+0200", worklog.GetProperty("started").GetString());
        Assert.Equal("Investigated and fixed", worklog.GetProperty("comment").GetString());
    }

    [Fact]
    public async Task AddWorklogSendsStartedAndRoundedSeconds()
    {
        var handler = new FakeHandler("{}");
        var client = new JiraClient(new HttpClient(handler));
        var config = new AppConfig { JiraHost = "https://jira.example.local", UserName = "rafa" };
        var started = new DateTimeOffset(2026, 4, 29, 10, 15, 0, TimeSpan.FromHours(2));

        await client.AddWorklogAsync(
            config,
            "pat-token",
            "10001",
            started,
            180,
            CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.LastRequest?.Method);
        Assert.Equal("/rest/api/2/issue/10001/worklog", handler.LastRequest?.RequestUri?.AbsolutePath);
        Assert.NotNull(handler.LastRequestBody);

        using var document = JsonDocument.Parse(handler.LastRequestBody!);
        var root = document.RootElement;
        Assert.Equal("2026-04-29T10:15:00.000+0200", root.GetProperty("started").GetString());
        Assert.Equal(180, root.GetProperty("timeSpentSeconds").GetInt32());
        Assert.False(root.TryGetProperty("comment", out _));
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<string> _jsonResponses;

        public FakeHandler(params string[] jsonResponses)
        {
            _jsonResponses = new Queue<string>(jsonResponses);
        }

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    _jsonResponses.Count > 0 ? _jsonResponses.Dequeue() : "{}",
                    Encoding.UTF8,
                    "application/json")
            };

            return response;
        }
    }
}

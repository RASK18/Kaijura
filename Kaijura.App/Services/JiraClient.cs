using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Kaijura.App.Models;

namespace Kaijura.App.Services;

public sealed class JiraClient : IJiraCommentReader
{
    private const int PageSize = 100;
    private const int CommentPageSize = 100;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;

    public JiraClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task ValidateAsync(AppConfig config, string token, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, BuildUri(config.JiraHost, "rest/api/2/myself"), token);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw await CreateExceptionAsync(response, "No se pudo validar el usuario de Jira.", cancellationToken);
        }
    }

    public async Task<JiraSearchResult> SearchAsync(AppConfig config, string token, CancellationToken cancellationToken)
    {
        var maxIssues = Math.Clamp(config.MaxIssues, 1, 5000);
        var issues = new List<JiraIssue>();
        var startAt = 0;
        var total = 0;

        do
        {
            var remaining = maxIssues - issues.Count;
            var pageSize = Math.Min(PageSize, remaining);
            if (pageSize <= 0)
            {
                break;
            }

            var payload = new
            {
                jql = config.Jql,
                startAt,
                maxResults = pageSize,
                fields = new[] { "summary", "status", "issuetype", "updated" }
            };

            using var request = CreateRequest(HttpMethod.Post, BuildUri(config.JiraHost, "rest/api/2/search"), token);
            request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw await CreateExceptionAsync(response, "No se pudo ejecutar la JQL configurada.", cancellationToken);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            total = root.GetProperty("total").GetInt32();

            if (!root.TryGetProperty("issues", out var rawIssues))
            {
                break;
            }

            foreach (var rawIssue in rawIssues.EnumerateArray())
            {
                issues.Add(ParseIssue(config.JiraHost, rawIssue));
            }

            startAt += rawIssues.GetArrayLength();
        }
        while (issues.Count < total && issues.Count < maxIssues);

        return new JiraSearchResult(issues, total, total > issues.Count);
    }

    public async Task<JiraIssue> GetIssueAsync(
        AppConfig config,
        string token,
        string issueIdOrKey,
        CancellationToken cancellationToken)
    {
        var path = $"rest/api/2/issue/{Uri.EscapeDataString(issueIdOrKey)}?fields=summary,status,issuetype,updated";
        using var request = CreateRequest(HttpMethod.Get, BuildUri(config.JiraHost, path), token);
        using var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw await CreateExceptionAsync(response, $"No se pudo recargar el ticket {issueIdOrKey}.", cancellationToken);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseIssue(config.JiraHost, document.RootElement);
    }

    public async Task<IReadOnlyList<JiraTransition>> GetTransitionsAsync(
        AppConfig config,
        string token,
        string issueIdOrKey,
        CancellationToken cancellationToken)
    {
        var path = $"rest/api/2/issue/{Uri.EscapeDataString(issueIdOrKey)}/transitions?expand=transitions.fields";
        using var request = CreateRequest(HttpMethod.Get, BuildUri(config.JiraHost, path), token);
        using var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw await CreateExceptionAsync(response, $"No se pudieron leer transiciones de {issueIdOrKey}.", cancellationToken);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("transitions", out var rawTransitions)
            || rawTransitions.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return rawTransitions.EnumerateArray().Select(ParseTransition).ToList();
    }

    public async Task TransitionIssueAsync(
        AppConfig config,
        string token,
        string issueIdOrKey,
        JiraTransitionUpdate transitionUpdate,
        CancellationToken cancellationToken)
    {
        var payload = BuildTransitionPayload(transitionUpdate);
        var path = $"rest/api/2/issue/{Uri.EscapeDataString(issueIdOrKey)}/transitions";
        using var request = CreateRequest(HttpMethod.Post, BuildUri(config.JiraHost, path), token);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateExceptionAsync(response, $"No se pudo cambiar el estado de {issueIdOrKey}.", cancellationToken);
        }
    }

    public async Task<JiraComment?> GetLatestRelevantCommentAsync(
        AppConfig config,
        string token,
        string issueIdOrKey,
        IReadOnlyCollection<string> ignoredAuthors,
        CancellationToken cancellationToken)
    {
        var startAt = 0;
        var total = 0;

        do
        {
            var path = $"rest/api/2/issue/{Uri.EscapeDataString(issueIdOrKey)}/comment";
            var uri = BuildUri(
                config.JiraHost,
                $"{path}?startAt={startAt}&maxResults={CommentPageSize}&orderBy=-created");

            using var request = CreateRequest(HttpMethod.Get, uri, token);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw await CreateExceptionAsync(response, $"No se pudieron leer comentarios de {issueIdOrKey}.", cancellationToken);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            total = root.TryGetProperty("total", out var totalElement) ? totalElement.GetInt32() : 0;

            if (!root.TryGetProperty("comments", out var comments))
            {
                return null;
            }

            foreach (var rawComment in comments.EnumerateArray())
            {
                var comment = ParseComment(rawComment);
                if (!IsIgnoredAuthor(comment, ignoredAuthors))
                {
                    return comment;
                }
            }

            startAt += comments.GetArrayLength();
        }
        while (startAt < total);

        return null;
    }

    public static string NormalizeHost(string host)
    {
        var normalized = host.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        if (!normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "https://" + normalized;
        }

        var uri = new Uri(normalized, UriKind.Absolute);
        var builder = new UriBuilder(uri)
        {
            Fragment = string.Empty,
            Query = string.Empty,
            Path = uri.AbsolutePath.TrimEnd('/')
        };

        return builder.Uri.ToString().TrimEnd('/');
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, string token)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("Kaijura/0.1");
        return request;
    }

    private static Uri BuildUri(string host, string relativePath)
    {
        var normalizedHost = NormalizeHost(host);
        var baseUri = new Uri(normalizedHost.EndsWith('/') ? normalizedHost : normalizedHost + "/");
        return new Uri(baseUri, relativePath);
    }

    private static JiraIssue ParseIssue(string jiraHost, JsonElement rawIssue)
    {
        var id = GetString(rawIssue, "id");
        var key = GetString(rawIssue, "key");
        var fields = rawIssue.GetProperty("fields");
        var summary = GetString(fields, "summary");
        var status = fields.TryGetProperty("status", out var statusElement)
            ? GetString(statusElement, "name")
            : string.Empty;
        var issueType = fields.TryGetProperty("issuetype", out var typeElement)
            ? GetString(typeElement, "name")
            : string.Empty;
        var updated = TryParseDate(GetString(fields, "updated"));
        var browseUrl = $"{NormalizeHost(jiraHost)}/browse/{Uri.EscapeDataString(key)}";

        return new JiraIssue(id, key, summary, status, issueType, browseUrl, updated);
    }

    private static JiraTransition ParseTransition(JsonElement rawTransition)
    {
        var to = rawTransition.TryGetProperty("to", out var toElement)
            ? toElement
            : default;
        var fields = new List<JiraTransitionField>();

        if (rawTransition.TryGetProperty("fields", out var rawFields) && rawFields.ValueKind == JsonValueKind.Object)
        {
            foreach (var rawField in rawFields.EnumerateObject())
            {
                fields.Add(ParseTransitionField(rawField.Name, rawField.Value));
            }
        }

        return new JiraTransition(
            GetString(rawTransition, "id"),
            GetString(rawTransition, "name"),
            GetString(to, "name"),
            fields);
    }

    private static JiraTransitionField ParseTransitionField(string id, JsonElement rawField)
    {
        var schema = rawField.TryGetProperty("schema", out var schemaElement)
            ? schemaElement
            : default;
        var operations = rawField.TryGetProperty("operations", out var rawOperations)
            && rawOperations.ValueKind == JsonValueKind.Array
                ? rawOperations
                    .EnumerateArray()
                    .Select(operation => operation.GetString() ?? string.Empty)
                    .Where(operation => !string.IsNullOrWhiteSpace(operation))
                    .ToList()
                : new List<string>();

        return new JiraTransitionField(
            id,
            GetString(rawField, "name"),
            rawField.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.True,
            GetString(schema, "type"),
            GetString(schema, "system"),
            GetString(schema, "items"),
            operations);
    }

    private static Dictionary<string, object> BuildTransitionPayload(JiraTransitionUpdate transitionUpdate)
    {
        var payload = new Dictionary<string, object>
        {
            ["transition"] = new Dictionary<string, string>
            {
                ["id"] = transitionUpdate.TransitionId
            }
        };

        var fields = new Dictionary<string, object>();
        var updates = new Dictionary<string, object>();

        foreach (var field in transitionUpdate.TextFields)
        {
            if (string.IsNullOrWhiteSpace(field.Key) || string.IsNullOrWhiteSpace(field.Value))
            {
                continue;
            }

            fields[field.Key] = field.Value.Trim();
        }

        if (!string.IsNullOrWhiteSpace(transitionUpdate.Comment))
        {
            updates["comment"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["add"] = new Dictionary<string, string>
                    {
                        ["body"] = transitionUpdate.Comment.Trim()
                    }
                }
            };
        }

        if (!string.IsNullOrWhiteSpace(transitionUpdate.WorklogTimeSpent))
        {
            var worklog = new Dictionary<string, string>
            {
                ["timeSpent"] = transitionUpdate.WorklogTimeSpent.Trim(),
                ["started"] = FormatJiraDate(transitionUpdate.WorklogStartedAt ?? DateTimeOffset.Now)
            };

            if (!string.IsNullOrWhiteSpace(transitionUpdate.WorklogComment))
            {
                worklog["comment"] = transitionUpdate.WorklogComment.Trim();
            }

            updates["worklog"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["add"] = worklog
                }
            };
        }

        if (fields.Count > 0)
        {
            payload["fields"] = fields;
        }

        if (updates.Count > 0)
        {
            payload["update"] = updates;
        }

        return payload;
    }

    private static string FormatJiraDate(DateTimeOffset value)
    {
        return value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture)
            + value.ToString("zzz", CultureInfo.InvariantCulture).Replace(":", string.Empty);
    }

    private static JiraComment ParseComment(JsonElement rawComment)
    {
        var author = rawComment.TryGetProperty("author", out var authorElement)
            ? authorElement
            : default;

        return new JiraComment(
            GetString(rawComment, "id"),
            GetString(author, "name"),
            GetString(author, "key"),
            GetString(author, "displayName"),
            GetString(author, "emailAddress"),
            TryParseDate(GetString(rawComment, "created")),
            TryParseDate(GetString(rawComment, "updated")));
    }

    private static bool IsIgnoredAuthor(JiraComment comment, IReadOnlyCollection<string> ignoredAuthors)
    {
        if (ignoredAuthors.Count == 0)
        {
            return false;
        }

        var authorValues = new[]
        {
            comment.AuthorName,
            comment.AuthorKey,
            comment.AuthorDisplayName,
            comment.AuthorEmailAddress
        };

        return ignoredAuthors.Any(ignored => authorValues.Any(author =>
            string.Equals(author.Trim(), ignored.Trim(), StringComparison.OrdinalIgnoreCase)));
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }

        return property.GetString() ?? string.Empty;
    }

    private static DateTimeOffset? TryParseDate(string value)
    {
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
            ? parsed
            : null;
    }

    private static async Task<JiraClientException> CreateExceptionAsync(
        HttpResponseMessage response,
        string fallback,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var detail = string.IsNullOrWhiteSpace(body) ? fallback : $"{fallback} Jira respondió {(int)response.StatusCode}: {body}";
        return new JiraClientException(detail, response.StatusCode);
    }
}

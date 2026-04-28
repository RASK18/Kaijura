using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Kaijura.App.Models;

namespace Kaijura.App.Services;

public sealed class JiraClient
{
    private const int PageSize = 100;
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

    private static string GetString(JsonElement element, string propertyName)
    {
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

using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Threading;
using Kaijura.App.Models;
using Kaijura.App.Services;
using Kaijura.App.Storage;
using Microsoft.Web.WebView2.Core;

namespace Kaijura.App;

public partial class MainWindow : Window
{
    private const string AppHost = "app.kaijura.local";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly LocalDataStore _store = new();
    private readonly ISecretProtector _secretProtector = new DpapiSecretProtector();
    private readonly JiraClient _jiraClient = new(new HttpClient { Timeout = TimeSpan.FromSeconds(25) });
    private readonly BoardSyncService _syncService = new();
    private readonly CommentSyncService _commentSyncService = new();
    private readonly UpdateService _updateService = new();
    private readonly DispatcherTimer _refreshTimer = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();

    private AppState _state = new();
    private ConnectionState _connection = new();
    private UpdateStatus _update = new();
    private string _activeView = "settings";
    private bool _webReady;
    private bool _initialRefreshStarted;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += (_, _) => _shutdown.Cancel();
        _refreshTimer.Tick += async (_, _) => await RefreshJiraAsync(isAutoRefresh: true);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _state = await _store.LoadAsync(_shutdown.Token);
        _connection = CreateInitialConnection();
        ConfigureRefreshTimer();

        await InitializeWebViewAsync();
        _ = CheckForUpdatesAsync();
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            await Browser.EnsureCoreWebView2Async();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"No se pudo iniciar WebView2. Comprueba que Microsoft Edge WebView2 Runtime está instalado.\n\n{ex.Message}",
                "Kaijura",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        var webRoot = Path.Combine(AppContext.BaseDirectory, "Web");
        Browser.CoreWebView2.SetVirtualHostNameToFolderMapping(
            AppHost,
            webRoot,
            CoreWebView2HostResourceAccessKind.DenyCors);

        Browser.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        Browser.CoreWebView2.NavigationStarting += OnNavigationStarting;
        Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;

#if !DEBUG
        Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
#endif

        Browser.Source = new Uri($"https://{AppHost}/index.html");
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || uri.Host == AppHost)
        {
            return;
        }

        e.Cancel = true;
        OpenExternalUrl(e.Uri);
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            var type = root.GetProperty("type").GetString() ?? string.Empty;
            var payload = root.TryGetProperty("payload", out var value) ? value : default;

            switch (type)
            {
                case "ready":
                    _webReady = true;
                    SendState();
                    await StartInitialRefreshAsync();
                    break;
                case "saveSettings":
                    await SaveSettingsAsync(payload);
                    break;
                case "refresh":
                    await RefreshJiraAsync(isAutoRefresh: false);
                    break;
                case "moveIssue":
                    await MoveIssueAsync(payload);
                    break;
                case "archiveIssue":
                    await ArchiveIssueAsync(payload);
                    break;
                case "markCommentRead":
                    await MarkCommentReadAsync(payload);
                    break;
                case "restoreIssue":
                    await RestoreIssueAsync(payload);
                    break;
                case "openIssue":
                    OpenIssue(payload);
                    break;
                case "setView":
                    _activeView = payload.GetProperty("view").GetString() ?? "board";
                    SendState();
                    break;
                case "installUpdate":
                    await InstallUpdateAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            _connection.Status = "blocked";
            _connection.Message = $"Acción no completada: {ex.Message}";
            _activeView = "settings";
            SendState();
        }
    }

    private async Task StartInitialRefreshAsync()
    {
        if (_initialRefreshStarted)
        {
            return;
        }

        _initialRefreshStarted = true;
        if (_state.Config.IsReadyForJira)
        {
            await RefreshJiraAsync(isAutoRefresh: false);
        }
    }

    private async Task SaveSettingsAsync(JsonElement payload)
    {
        var settings = payload.Deserialize<SettingsPayload>(JsonOptions)
            ?? throw new InvalidOperationException("Configuración vacía.");

        _state.Config.JiraHost = JiraClient.NormalizeHost(settings.JiraHost);
        _state.Config.UserName = settings.UserName.Trim();
        _state.Config.Jql = string.IsNullOrWhiteSpace(settings.Jql) ? "ORDER BY updated DESC" : settings.Jql.Trim();
        _state.Config.TaskIssueTypes = CleanList(settings.TaskIssueTypes ?? []);
        _state.Config.IncidentIssueTypes = CleanList(settings.IncidentIssueTypes ?? []);
        _state.Config.IgnoredCommentAuthors = CleanList(settings.IgnoredCommentAuthors ?? []);
        _state.Config.RefreshMinutes = Math.Clamp(settings.RefreshMinutes, 1, 120);
        _state.Config.MaxIssues = Math.Clamp(settings.MaxIssues, 1, 5000);
        _state.Config.UpdateRepositoryUrl = settings.UpdateRepositoryUrl.Trim();

        if (!string.IsNullOrWhiteSpace(settings.Token))
        {
            _state.Config.EncryptedToken = _secretProtector.Protect(settings.Token.Trim());
        }

        if (string.IsNullOrWhiteSpace(_state.Config.EncryptedToken))
        {
            throw new InvalidOperationException("El token personal de Jira es obligatorio.");
        }

        await _store.SaveAsync(_state, _shutdown.Token);
        _connection = new ConnectionState
        {
            Status = "refreshing",
            Message = "Validando Jira.",
            IsConfigured = true,
            IsRefreshing = true
        };
        _activeView = "board";
        ConfigureRefreshTimer();
        SendState();
        await RefreshJiraAsync(isAutoRefresh: false);
        _ = CheckForUpdatesAsync();
    }

    private async Task RefreshJiraAsync(bool isAutoRefresh)
    {
        if (!_state.Config.IsReadyForJira)
        {
            _connection = CreateInitialConnection();
            _activeView = "settings";
            SendState();
            return;
        }

        if (!await _refreshLock.WaitAsync(0, _shutdown.Token))
        {
            return;
        }

        try
        {
            _connection.IsRefreshing = true;
            _connection.Status = "refreshing";
            _connection.Message = isAutoRefresh ? "Actualizando desde Jira." : "Sincronizando Jira.";
            SendState();

            var token = _secretProtector.Unprotect(_state.Config.EncryptedToken);
            await _jiraClient.ValidateAsync(_state.Config, token, _shutdown.Token);
            var result = await _jiraClient.SearchAsync(_state.Config, token, _shutdown.Token);
            var summary = _syncService.Sync(_state, result, DateTimeOffset.Now);
            var commentSummary = await _commentSyncService.SyncKanbanCommentsAsync(
                _state,
                _jiraClient,
                token,
                _shutdown.Token);

            _state.LastSuccessfulSyncAt = DateTimeOffset.Now;
            _connection.Status = "connected";
            _connection.Message = BuildSyncMessage(summary, commentSummary);
            _connection.IsConfigured = true;
            _activeView = "board";
            ConfigureRefreshTimer();
            await _store.SaveAsync(_state, _shutdown.Token);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _connection.Status = "blocked";
            _connection.Message = FriendlyJiraError(ex);
            _connection.IsConfigured = _state.Config.IsReadyForJira;
            _activeView = "settings";
            _refreshTimer.Stop();
        }
        finally
        {
            _connection.IsRefreshing = false;
            _refreshLock.Release();
            SendState();
        }
    }

    private async Task MoveIssueAsync(JsonElement payload)
    {
        var request = payload.Deserialize<MoveIssuePayload>(JsonOptions)
            ?? throw new InvalidOperationException("Movimiento vacío.");

        var section = ParseSection(request.Section);
        var column = ParseColumn(request.Column);
        _syncService.MoveIssue(_state, request.IssueId, section, column, request.OrderedIssueIds ?? []);
        await _store.SaveAsync(_state, _shutdown.Token);
        SendState();
    }

    private async Task ArchiveIssueAsync(JsonElement payload)
    {
        var issueId = payload.GetProperty("issueId").GetString() ?? string.Empty;
        if (_syncService.ArchiveIssue(_state, issueId, DateTimeOffset.Now))
        {
            await _store.SaveAsync(_state, _shutdown.Token);
        }

        SendState();
    }

    private async Task MarkCommentReadAsync(JsonElement payload)
    {
        var issueId = payload.GetProperty("issueId").GetString() ?? string.Empty;
        if (_commentSyncService.MarkCommentRead(_state, issueId))
        {
            await _store.SaveAsync(_state, _shutdown.Token);
        }

        SendState();
    }

    private async Task RestoreIssueAsync(JsonElement payload)
    {
        var issueId = payload.GetProperty("issueId").GetString() ?? string.Empty;
        if (_syncService.RestoreIssue(_state, issueId))
        {
            await _store.SaveAsync(_state, _shutdown.Token);
        }

        SendState();
    }

    private void OpenIssue(JsonElement payload)
    {
        var issueId = payload.GetProperty("issueId").GetString() ?? string.Empty;
        var issue = _state.Issues.FirstOrDefault(candidate => candidate.JiraId == issueId);
        if (!string.IsNullOrWhiteSpace(issue?.BrowseUrl))
        {
            OpenExternalUrl(issue.BrowseUrl);
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        var status = await _updateService.CheckAsync(_state.Config.UpdateRepositoryUrl, _shutdown.Token);
        await Dispatcher.InvokeAsync(() =>
        {
            _update = status;
            SendState();
        });
    }

    private async Task InstallUpdateAsync()
    {
        _update = new UpdateStatus { Status = "downloading", Message = "Preparando actualización.", CanInstall = false };
        SendState();

        var status = await _updateService.InstallAsync(progress =>
        {
            Dispatcher.Invoke(() =>
            {
                _update = progress;
                SendState();
            });
        }, _shutdown.Token);

        _update = status;
        SendState();
    }

    private void ConfigureRefreshTimer()
    {
        _refreshTimer.Stop();
        _refreshTimer.Interval = TimeSpan.FromMinutes(Math.Clamp(_state.Config.RefreshMinutes, 1, 120));

        if (_state.Config.IsReadyForJira && _connection.Status != "blocked")
        {
            _refreshTimer.Start();
        }
    }

    private ConnectionState CreateInitialConnection()
    {
        var configured = _state.Config.IsReadyForJira;
        _activeView = configured ? "board" : "settings";

        return new ConnectionState
        {
            Status = configured ? "idle" : "unconfigured",
            Message = configured ? "Pendiente de sincronización." : "Configura Jira para empezar.",
            IsConfigured = configured
        };
    }

    private void SendState()
    {
        if (!_webReady || Browser.CoreWebView2 is null)
        {
            return;
        }

        var message = new
        {
            type = "state",
            payload = BuildUiState()
        };

        Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
    }

    private object BuildUiState()
    {
        return new
        {
            activeView = _activeView,
            connection = _connection,
            update = _update,
            columns = Enum.GetValues<BoardColumn>().Select(column => new
            {
                id = ToClient(column),
                title = ColumnTitle(column)
            }),
            config = new
            {
                _state.Config.JiraHost,
                _state.Config.UserName,
                _state.Config.Jql,
                _state.Config.TaskIssueTypes,
                _state.Config.IncidentIssueTypes,
                _state.Config.IgnoredCommentAuthors,
                _state.Config.RefreshMinutes,
                _state.Config.MaxIssues,
                _state.Config.UpdateRepositoryUrl,
                hasToken = !string.IsNullOrWhiteSpace(_state.Config.EncryptedToken)
            },
            lastSuccessfulSyncAt = _state.LastSuccessfulSyncAt,
            issues = _state.Issues
                .OrderBy(issue => issue.SortOrder)
                .ThenBy(issue => issue.Key)
                .Select(issue => new
                {
                    id = issue.JiraId,
                    issue.Key,
                    issue.Summary,
                    issue.JiraStatus,
                    issue.IssueType,
                    kind = ToClient(issue.Kind),
                    section = ToClient(issue.Section),
                    column = ToClient(issue.Column),
                    issue.SortOrder,
                    issue.IsMissing,
                    issue.MissingSince,
                    issue.ArchivedAt,
                    issue.HasUnreadComment,
                    issue.LastRelevantCommentAuthor,
                    issue.LastRelevantCommentAt,
                    issue.BrowseUrl
                })
        };
    }

    private static string BuildSyncMessage(SyncSummary summary, CommentSyncSummary commentSummary)
    {
        var message = $"{summary.VisibleCount} visibles, {summary.MissingCount} ocultos por JQL.";

        if (summary.UnmappedCount > 0)
        {
            message += $" {summary.UnmappedCount} sin mapear.";
        }

        if (summary.Truncated)
        {
            message += " JQL truncada por límite configurado.";
        }

        if (commentSummary.FailedCount > 0)
        {
            message += $" {commentSummary.FailedCount} tickets sin comprobar comentarios.";
        }

        return message;
    }

    private static string FriendlyJiraError(Exception ex)
    {
        return ex switch
        {
            JiraClientException jiraEx => jiraEx.Message,
            CryptographicException => "No se pudo descifrar el token guardado. Reintroduce el token en configuración.",
            HttpRequestException httpEx => $"No se pudo conectar con Jira: {httpEx.Message}",
            TaskCanceledException => "Jira no respondió a tiempo. Revisa conexión, VPN o proxy.",
            UriFormatException => "El host de Jira no tiene un formato válido.",
            _ => $"No se pudo usar Jira: {ex.Message}"
        };
    }

    private static List<string> CleanList(IEnumerable<string> values)
    {
        return values
            .Select(value => value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static BoardSection ParseSection(string value)
    {
        return value.Equals("board", StringComparison.OrdinalIgnoreCase)
            ? BoardSection.Board
            : BoardSection.Backlog;
    }

    private static BoardColumn ParseColumn(string value)
    {
        return Enum.TryParse<BoardColumn>(value, ignoreCase: true, out var column)
            ? column
            : BoardColumn.ToDo;
    }

    private static string ToClient(BoardSection section)
    {
        return section switch
        {
            BoardSection.Board => "board",
            BoardSection.Archived => "archived",
            BoardSection.Missing => "missing",
            _ => "backlog"
        };
    }

    private static string ToClient(IssueKind kind)
    {
        return kind switch
        {
            IssueKind.Task => "task",
            IssueKind.Incident => "incident",
            _ => "unmapped"
        };
    }

    private static string ToClient(BoardColumn column)
    {
        return column.ToString();
    }

    private static string ColumnTitle(BoardColumn column)
    {
        return column switch
        {
            BoardColumn.ToDo => "ToDo",
            BoardColumn.Progress => "Progress",
            BoardColumn.Dev => "Dev",
            BoardColumn.Test => "Test",
            BoardColumn.Ready => "Ready",
            _ => column.ToString()
        };
    }

    private static void OpenExternalUrl(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private sealed record SettingsPayload(
        string JiraHost,
        string UserName,
        string Token,
        string Jql,
        List<string> TaskIssueTypes,
        List<string> IncidentIssueTypes,
        List<string> IgnoredCommentAuthors,
        int RefreshMinutes,
        int MaxIssues,
        string UpdateRepositoryUrl);

    private sealed record MoveIssuePayload(
        string IssueId,
        string Section,
        string Column,
        string[] OrderedIssueIds);
}

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Kaijura.App.Models;
using Kaijura.App.Services;
using Kaijura.App.Storage;
using Microsoft.Web.WebView2.Core;

namespace Kaijura.App;

public partial class MainWindow : Window
{
    private const string AppHost = "app.kaijura.local";

    private static readonly Brush ConnectedBrush = CreateBrush("#00d4aa");
    private static readonly Brush WarningBrush = CreateBrush("#ffb85c");
    private static readonly Brush DangerBrush = CreateBrush("#ff6b7a");
    private static readonly Brush QuietBrush = CreateBrush("#73737b");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly LocalDataStore _store = new();
    private readonly ISecretProtector _secretProtector = new DpapiSecretProtector();
    private readonly JiraClient _jiraClient = new(new HttpClient { Timeout = TimeSpan.FromSeconds(25) });
    private readonly BoardSyncService _syncService = new();
    private readonly AutomationRuleService _automationService = new();
    private readonly JiraTransitionAnalyzer _transitionAnalyzer = new();
    private readonly CommentSyncService _commentSyncService = new();
    private readonly TimeTrackingService _timeTrackingService = new();
    private readonly UpdateService _updateService = new();
    private readonly DispatcherTimer _refreshTimer = new();
    private readonly DispatcherTimer _relativeTimeTimer = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();

    private AppState _state = new();
    private ConnectionState _connection = new();
    private UpdateStatus _update = new();
    private string _activeView = "settings";
    private bool _webReady;
    private bool _initialRefreshStarted;
    private bool _startupTrackerPromptShown;
    private bool _allowCloseAfterTrackerStop;
    private bool _closingTrackerInProgress;

    public MainWindow()
    {
        InitializeComponent();
        StateChanged += OnWindowStateChanged;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _shutdown.Cancel();
            _relativeTimeTimer.Stop();
        };
        _refreshTimer.Tick += async (_, _) => await RefreshJiraAsync(isAutoRefresh: true);
        _relativeTimeTimer.Interval = TimeSpan.FromSeconds(30);
        _relativeTimeTimer.Tick += (_, _) => UpdateTitlebarStatus();
        _relativeTimeTimer.Start();
        UpdateMaximizeButtonState();
        UpdateNavigationButtonState();
        UpdateTitlebarStatus();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowCloseAfterTrackerStop || _state.ActiveTimeTracker is null)
        {
            return;
        }

        e.Cancel = true;
        if (_closingTrackerInProgress)
        {
            return;
        }

        var tracker = _state.ActiveTimeTracker;
        _closingTrackerInProgress = true;

        SendWebMessage("showCloseTrackerConfirmation", new
        {
            issueId = tracker.IssueId,
            issueKey = tracker.IssueKey
        });
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        UpdateMaximizeButtonState();
    }

    private void OnMinimizeButtonClick(object sender, RoutedEventArgs e)
    {
        SystemCommands.MinimizeWindow(this);
    }

    private void OnMaximizeButtonClick(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
            return;
        }

        SystemCommands.MaximizeWindow(this);
    }

    private void OnCloseButtonClick(object sender, RoutedEventArgs e)
    {
        SystemCommands.CloseWindow(this);
    }

    private void OnNavigationButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string view })
        {
            SetActiveView(view);
        }
    }

    private async void OnTitlebarRefreshButtonClick(object sender, RoutedEventArgs e)
    {
        await RefreshJiraAsync(isAutoRefresh: false);
    }

    private void UpdateMaximizeButtonState()
    {
        var isMaximized = WindowState == WindowState.Maximized;
        var actionName = isMaximized ? "Restaurar" : "Maximizar";

        MaximizeButton.Content = isMaximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = actionName;
        AutomationProperties.SetName(MaximizeButton, actionName);
    }

    private void SetActiveView(string view)
    {
        if (!IsKnownView(view))
        {
            view = "board";
        }

        _activeView = view;
        SendState();
    }

    private static bool IsKnownView(string view)
    {
        return view is "board" or "archive" or "missing" or "unmapped" or "settings";
    }

    private void UpdateNavigationButtonState()
    {
        ToggleButton[] buttons =
        [
            BoardNavigationButton,
            ArchiveNavigationButton,
            MissingNavigationButton,
            UnmappedNavigationButton,
            SettingsNavigationButton
        ];

        foreach (var button in buttons)
        {
            button.IsChecked = string.Equals(button.Tag as string, _activeView, StringComparison.Ordinal);
        }
    }

    private void UpdateTitlebarStatus()
    {
        var status = _connection.Status ?? "unconfigured";
        var isRefreshing = status == "refreshing";
        var hasLastSync = _state.LastSuccessfulSyncAt is not null;

        TitlebarConnectionStatusDot.Fill = status switch
        {
            "connected" => ConnectedBrush,
            "refreshing" => WarningBrush,
            "blocked" => DangerBrush,
            _ => QuietBrush
        };

        var statusText = isRefreshing
            ? "Sincronizando con Jira..."
            : status == "blocked"
                ? "Error"
            : hasLastSync
                ? $"Actualizado: {FormatRelativeTime(_state.LastSuccessfulSyncAt!.Value)}"
                : string.Empty;

        TitlebarStatusTextBlock.Text = statusText;
        TitlebarStatusTextBlock.Visibility = string.IsNullOrWhiteSpace(statusText)
            ? Visibility.Collapsed
            : Visibility.Visible;
        TitlebarRefreshButton.IsEnabled = !_connection.IsRefreshing && _connection.IsConfigured;

        var connectionLabel = ConnectionText(status);
        TitlebarConnectionStatusDot.ToolTip = connectionLabel;
        AutomationProperties.SetName(TitlebarConnectionStatusDot, connectionLabel);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _state = await _store.LoadAsync(_shutdown.Token);
        var seededIgnoredCommentAuthor = _state.Config.SeedIgnoredCommentAuthorsWithUserName();
        _connection = CreateInitialConnection();
        ConfigureRefreshTimer();
        UpdateTitlebarStatus();
        if (seededIgnoredCommentAuthor)
        {
            await _store.SaveAsync(_state, _shutdown.Token);
        }

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
                    ShowPendingTrackerPromptIfNeeded();
                    await StartInitialRefreshAsync();
                    break;
                case "saveSettings":
                    await SaveSettingsAsync(payload);
                    break;
                case "simulateAutomationRules":
                    SimulateAutomationRules(payload);
                    break;
                case "refresh":
                    await RefreshJiraAsync(isAutoRefresh: false);
                    break;
                case "moveIssue":
                    await MoveIssueAsync(payload);
                    break;
                case "startTracker":
                    await StartTrackerAsync(payload);
                    break;
                case "stopTracker":
                    await StopTrackerAsync(payload);
                    break;
                case "confirmCloseWithTracker":
                    await ConfirmCloseWithTrackerAsync();
                    break;
                case "cancelCloseWithTracker":
                    CancelCloseWithTracker();
                    break;
                case "registerPendingTracker":
                    await RegisterPendingTrackerAsync();
                    break;
                case "discardPendingTracker":
                    await DiscardPendingTrackerAsync();
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
                case "loadTransitions":
                    await LoadTransitionsAsync(payload);
                    break;
                case "changeIssueStatus":
                    await ChangeIssueStatusAsync(payload);
                    break;
                case "setView":
                    SetActiveView(payload.GetProperty("view").GetString() ?? "board");
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
        _state.Config.AutomationRules = CleanAutomationRules(settings.AutomationRules ?? []);
        _state.Config.SeedIgnoredCommentAuthorsWithUserName();
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

    private void SimulateAutomationRules(JsonElement payload)
    {
        var request = payload.Deserialize<AutomationRulesPayload>(JsonOptions)
            ?? throw new InvalidOperationException("Reglas vacias.");
        var rules = CleanAutomationRules(request.AutomationRules ?? []);
        var result = _automationService.Simulate(_state, rules, DateTimeOffset.Now);

        SendWebMessage("automationSimulation", new
        {
            total = result.Applications.Count,
            applications = result.Applications.Take(50)
        });
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
            _connection.Message = "Sincronizando con Jira...";
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
            var automationResult = _automationService.ApplyPending(_state, DateTimeOffset.Now);

            _state.LastSuccessfulSyncAt = DateTimeOffset.Now;
            _connection.Status = "connected";
            _connection.Message = BuildSyncMessage(summary, commentSummary, automationResult);
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
        if (ShouldStopActiveTrackerForMove(request.IssueId, section, column))
        {
            var lockTaken = false;
            try
            {
                await _refreshLock.WaitAsync(_shutdown.Token);
                lockTaken = true;

                if (ShouldStopActiveTrackerForMove(request.IssueId, section, column))
                {
                    await StopActiveTrackerCoreAsync(request.IssueId, DateTimeOffset.Now, saveState: false, _shutdown.Token);
                }

                _syncService.MoveIssue(_state, request.IssueId, section, column, request.OrderedIssueIds ?? []);
                await _store.SaveAsync(_state, _shutdown.Token);
                SendTrackerResult(request.IssueId);
                SendState();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SendTrackerError(request.IssueId, $"No se pudo mover el ticket: {FriendlyJiraError(ex)}");
                SendState();
            }
            finally
            {
                if (lockTaken)
                {
                    _refreshLock.Release();
                }
            }

            return;
        }

        _syncService.MoveIssue(_state, request.IssueId, section, column, request.OrderedIssueIds ?? []);
        await _store.SaveAsync(_state, _shutdown.Token);
        SendState();
    }

    private async Task StartTrackerAsync(JsonElement payload)
    {
        var issueId = payload.TryGetProperty("issueId", out var issueIdElement)
            ? issueIdElement.GetString() ?? string.Empty
            : string.Empty;
        var replaceActive = payload.TryGetProperty("replaceActive", out var replaceElement)
            && replaceElement.ValueKind == JsonValueKind.True;
        var lockTaken = false;

        try
        {
            await _refreshLock.WaitAsync(_shutdown.Token);
            lockTaken = true;

            var issue = FindIssue(issueId)
                ?? throw new InvalidOperationException("No se encontro el ticket en Kaijura.");
            if (issue.Section != BoardSection.Board || issue.Column != BoardColumn.Progress || issue.IsMissing)
            {
                throw new InvalidOperationException("El tracker solo puede iniciarse en tickets de En progreso.");
            }

            if (_state.ActiveTimeTracker is not null)
            {
                if (_timeTrackingService.IsActiveFor(_state, issue.JiraId))
                {
                    SendTrackerResult(issue.JiraId);
                    SendState();
                    return;
                }

                if (!replaceActive)
                {
                    throw new InvalidOperationException("Ya hay un tracker iniciado.");
                }

                await StopActiveTrackerCoreAsync(null, DateTimeOffset.Now, saveState: false, _shutdown.Token);
            }

            _timeTrackingService.Start(_state, issue, DateTimeOffset.Now);
            await _store.SaveAsync(_state, _shutdown.Token);
            SendTrackerResult(issue.JiraId);
            SendState();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SendTrackerError(issueId, $"No se pudo iniciar el tracker: {FriendlyJiraError(ex)}");
            SendState();
        }
        finally
        {
            if (lockTaken)
            {
                _refreshLock.Release();
            }
        }
    }

    private async Task StopTrackerAsync(JsonElement payload)
    {
        var issueId = payload.TryGetProperty("issueId", out var issueIdElement)
            ? issueIdElement.GetString() ?? string.Empty
            : string.Empty;

        try
        {
            await StopActiveTrackerWithLockAsync(issueId, DateTimeOffset.Now, saveState: true);
            SendTrackerResult(issueId);
            SendState();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SendTrackerError(issueId, $"No se pudo parar el tracker: {FriendlyJiraError(ex)}");
            SendState();
        }
    }

    private async Task ConfirmCloseWithTrackerAsync()
    {
        try
        {
            await StopActiveTrackerWithLockAsync(null, DateTimeOffset.Now, saveState: true);
            _allowCloseAfterTrackerStop = true;
            Close();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _closingTrackerInProgress = false;
            SendWebMessage("closeTrackerError", new
            {
                message = $"No se pudo registrar el tiempo en Jira. La aplicacion seguira abierta. {FriendlyJiraError(ex)}"
            });
            SendState();
        }
    }

    private void CancelCloseWithTracker()
    {
        _closingTrackerInProgress = false;
    }

    private void ShowPendingTrackerPromptIfNeeded()
    {
        var tracker = _state.ActiveTimeTracker;
        if (_startupTrackerPromptShown || tracker is null)
        {
            return;
        }

        _startupTrackerPromptShown = true;
        SendWebMessage("showPendingTrackerConfirmation", new
        {
            issueId = tracker.IssueId,
            issueKey = tracker.IssueKey
        });
    }

    private async Task RegisterPendingTrackerAsync()
    {
        try
        {
            await StopActiveTrackerWithLockAsync(null, DateTimeOffset.Now, saveState: true);
            SendWebMessage("pendingTrackerResult", new { });
            SendState();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SendWebMessage("pendingTrackerError", new
            {
                message = $"No se pudo registrar el tracker pendiente. Seguira activo para reintentar. {FriendlyJiraError(ex)}"
            });
            SendState();
        }
    }

    private async Task DiscardPendingTrackerAsync()
    {
        _timeTrackingService.Discard(_state);
        await _store.SaveAsync(_state, _shutdown.Token);
        SendWebMessage("pendingTrackerResult", new { });
        SendState();
    }

    private bool ShouldStopActiveTrackerForMove(string issueId, BoardSection targetSection, BoardColumn targetColumn)
    {
        return _timeTrackingService.ShouldStopForMove(_state, issueId, targetSection, targetColumn);
    }

    private async Task<TimeTrackerWorklog?> StopActiveTrackerWithLockAsync(
        string? expectedIssueId,
        DateTimeOffset stoppedAt,
        bool saveState)
    {
        await _refreshLock.WaitAsync(_shutdown.Token);
        try
        {
            return await StopActiveTrackerCoreAsync(expectedIssueId, stoppedAt, saveState, _shutdown.Token);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<TimeTrackerWorklog?> StopActiveTrackerCoreAsync(
        string? expectedIssueId,
        DateTimeOffset stoppedAt,
        bool saveState,
        CancellationToken cancellationToken)
    {
        if (_state.ActiveTimeTracker is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(expectedIssueId)
            && !_timeTrackingService.IsActiveFor(_state, expectedIssueId))
        {
            throw new InvalidOperationException("El tracker activo pertenece a otro ticket.");
        }

        if (!_state.Config.IsReadyForJira)
        {
            throw new InvalidOperationException("Configura Jira antes de registrar tiempo.");
        }

        var jiraToken = _secretProtector.Unprotect(_state.Config.EncryptedToken);
        var worklog = await _timeTrackingService.StopActiveAsync(
            _state,
            async (entry, token) => await _jiraClient.AddWorklogAsync(
                _state.Config,
                jiraToken,
                entry.IssueId,
                entry.StartedAt,
                entry.TimeSpentSeconds,
                token),
            stoppedAt,
            cancellationToken);

        if (saveState)
        {
            await _store.SaveAsync(_state, cancellationToken);
        }

        return worklog;
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

    private async Task LoadTransitionsAsync(JsonElement payload)
    {
        var issueId = payload.TryGetProperty("issueId", out var issueIdElement)
            ? issueIdElement.GetString() ?? string.Empty
            : string.Empty;

        try
        {
            var issue = FindIssue(issueId)
                ?? throw new InvalidOperationException("No se encontro el ticket en Kaijura.");
            var token = _secretProtector.Unprotect(_state.Config.EncryptedToken);
            var transitions = await _jiraClient.GetTransitionsAsync(_state.Config, token, issue.JiraId, _shutdown.Token);
            var options = _transitionAnalyzer.BuildOptions(transitions);

            SendWebMessage("issueTransitions", new
            {
                issueId = issue.JiraId,
                transitions = options
            });
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            SendTransitionError(issueId, $"No se pudieron cargar transiciones: {FriendlyJiraError(ex)}");
        }
    }

    private async Task ChangeIssueStatusAsync(JsonElement payload)
    {
        var issueId = payload.TryGetProperty("issueId", out var issueIdElement)
            ? issueIdElement.GetString() ?? string.Empty
            : string.Empty;
        var lockTaken = false;

        try
        {
            var request = payload.Deserialize<ChangeIssueStatusPayload>(JsonOptions)
                ?? throw new InvalidOperationException("Cambio de estado vacio.");

            await _refreshLock.WaitAsync(_shutdown.Token);
            lockTaken = true;

            var issue = FindIssue(request.IssueId)
                ?? throw new InvalidOperationException("No se encontro el ticket en Kaijura.");
            var token = _secretProtector.Unprotect(_state.Config.EncryptedToken);
            var transitionUpdate = new JiraTransitionUpdate(
                request.TransitionId,
                request.Comment ?? string.Empty,
                request.WorklogTimeSpent ?? string.Empty,
                request.WorklogComment ?? string.Empty,
                string.IsNullOrWhiteSpace(request.WorklogTimeSpent) ? null : DateTimeOffset.Now,
                request.TextFields ?? new Dictionary<string, string>(),
                request.SelectFields ?? new Dictionary<string, JiraTransitionAllowedValue>());

            await _jiraClient.TransitionIssueAsync(_state.Config, token, issue.JiraId, transitionUpdate, _shutdown.Token);
            var updatedIssue = await _jiraClient.GetIssueAsync(_state.Config, token, issue.JiraId, _shutdown.Token);
            var now = DateTimeOffset.Now;
            _syncService.UpdateIssueFromJira(_state, updatedIssue, now);
            _automationService.ApplyPending(_state, now);
            await _store.SaveAsync(_state, _shutdown.Token);

            SendState();
            SendWebMessage("transitionResult", new
            {
                issueId = issue.JiraId,
                jiraStatus = updatedIssue.JiraStatus
            });
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            SendTransitionError(issueId, $"No se pudo cambiar el estado: {FriendlyJiraError(ex)}");
        }
        finally
        {
            if (lockTaken)
            {
                _refreshLock.Release();
            }
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

    private IssueState? FindIssue(string issueId)
    {
        return _state.Issues.FirstOrDefault(issue =>
            string.Equals(issue.JiraId, issueId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(issue.Key, issueId, StringComparison.OrdinalIgnoreCase));
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
        UpdateNavigationButtonState();
        UpdateTitlebarStatus();

        SendWebMessage("state", BuildUiState());
    }

    private void SendTransitionError(string issueId, string message)
    {
        SendWebMessage("transitionError", new
        {
            issueId,
            message
        });
    }

    private void SendTrackerResult(string issueId)
    {
        SendWebMessage("trackerResult", new
        {
            issueId
        });
    }

    private void SendTrackerError(string issueId, string message)
    {
        SendWebMessage("trackerError", new
        {
            issueId,
            message
        });
    }

    private void SendWebMessage(string type, object payload)
    {
        if (!_webReady || Browser.CoreWebView2 is null)
        {
            return;
        }

        var message = new
        {
            type,
            payload
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
            activeTracker = _state.ActiveTimeTracker is null
                ? null
                : new
                {
                    issueId = _state.ActiveTimeTracker.IssueId,
                    issueKey = _state.ActiveTimeTracker.IssueKey,
                    startedAt = _state.ActiveTimeTracker.StartedAt
                },
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
                _state.Config.AutomationRules,
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
                    issue.JiraUpdatedAt,
                    issue.HasUnreadComment,
                    issue.LastRelevantCommentAuthor,
                    issue.LastRelevantCommentAt,
                    issue.BrowseUrl
                })
        };
    }

    private static string BuildSyncMessage(
        SyncSummary summary,
        CommentSyncSummary commentSummary,
        AutomationRuleResult automationResult)
    {
        List<string> messages = [];

        if (summary.Truncated)
        {
            messages.Add("JQL truncada por limite configurado.");
        }

        if (commentSummary.FailedCount > 0)
        {
            messages.Add($"{commentSummary.FailedCount} tickets sin comprobar comentarios.");
        }

        if (automationResult.Applications.Count > 0)
        {
            messages.Add($"{automationResult.Applications.Count} reglas aplicadas.");
        }

        return string.Join(" ", messages);
    }

    private static string ConnectionText(string status)
    {
        return status switch
        {
            "connected" => "Conectado",
            "refreshing" => "Sincronizando",
            "blocked" => "Bloqueado",
            "idle" => "Preparado",
            _ => "Sin configurar"
        };
    }

    private static string FormatRelativeTime(DateTimeOffset value)
    {
        var elapsed = DateTimeOffset.Now - value;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        if (elapsed.TotalSeconds < 60)
        {
            return "hace unos segundos";
        }

        if (elapsed.TotalMinutes < 60)
        {
            var minutes = Math.Max(1, (int)elapsed.TotalMinutes);
            return $"hace {minutes} {(minutes == 1 ? "minuto" : "minutos")}";
        }

        if (elapsed.TotalHours < 24)
        {
            var hours = Math.Max(1, (int)elapsed.TotalHours);
            return $"hace {hours} {(hours == 1 ? "hora" : "horas")}";
        }

        var days = Math.Max(1, (int)elapsed.TotalDays);
        return $"hace {days} {(days == 1 ? "dia" : "dias")}";
    }

    private static Brush CreateBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)!);
        brush.Freeze();
        return brush;
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

    private static List<AutomationRule> CleanAutomationRules(IEnumerable<AutomationRule> rules)
    {
        return rules.Select(rule => new AutomationRule
            {
                Id = string.IsNullOrWhiteSpace(rule.Id) ? Guid.NewGuid().ToString("N") : rule.Id.Trim(),
                Name = string.IsNullOrWhiteSpace(rule.Name) ? "Regla sin nombre" : rule.Name.Trim(),
                IsEnabled = rule.IsEnabled,
                Trigger = rule.Trigger,
                IssueScope = rule.IssueScope,
                CurrentLocation = rule.CurrentLocation,
                Conditions = CleanAutomationConditions(rule.Conditions ?? []),
                Action = new AutomationAction
                {
                    Destination = rule.Action?.Destination ?? AutomationDestination.ToDo
                },
                StopProcessing = rule.StopProcessing
            })
            .ToList();
    }

    private static List<AutomationCondition> CleanAutomationConditions(IEnumerable<AutomationCondition> conditions)
    {
        return conditions
            .Select(CleanAutomationCondition)
            .Where(condition => condition is not null)
            .Cast<AutomationCondition>()
            .ToList();
    }

    private static AutomationCondition? CleanAutomationCondition(AutomationCondition condition)
    {
        if (condition.Field is AutomationConditionField.JiraStatus or AutomationConditionField.IssueType)
        {
            var values = CleanList(condition.Values ?? []);
            return values.Count == 0
                ? null
                : new AutomationCondition
                {
                    Field = condition.Field,
                    Operator = condition.Operator == AutomationConditionOperator.IsNotAnyOf
                        ? AutomationConditionOperator.IsNotAnyOf
                        : AutomationConditionOperator.IsAnyOf,
                    Values = values
                };
        }

        if (condition.Field == AutomationConditionField.HasUnreadComment)
        {
            return new AutomationCondition
            {
                Field = condition.Field,
                Operator = condition.Operator == AutomationConditionOperator.IsNot
                    ? AutomationConditionOperator.IsNot
                    : AutomationConditionOperator.Is,
                BoolValue = condition.BoolValue
            };
        }

        return condition.Days <= 0
            ? null
            : new AutomationCondition
            {
                Field = condition.Field,
                Operator = AutomationConditionOperator.MoreThanDaysAgo,
                Days = condition.Days
            };
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
            BoardColumn.ToDo => "Por hacer",
            BoardColumn.Progress => "En progreso",
            BoardColumn.PendingQa => "Pendiente QA",
            BoardColumn.ValidatedQa => "Validado QA",
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
        List<AutomationRule>? AutomationRules,
        int RefreshMinutes,
        int MaxIssues,
        string UpdateRepositoryUrl);

    private sealed record AutomationRulesPayload(List<AutomationRule>? AutomationRules);

    private sealed record MoveIssuePayload(
        string IssueId,
        string Section,
        string Column,
        string[] OrderedIssueIds);

    private sealed record ChangeIssueStatusPayload(
        string IssueId,
        string TransitionId,
        string? Comment,
        string? WorklogTimeSpent,
        string? WorklogComment,
        Dictionary<string, string>? TextFields,
        Dictionary<string, JiraTransitionAllowedValue>? SelectFields);
}

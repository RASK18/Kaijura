using Kaijura.App.Models;
using Kaijura.App.Services;
using Kaijura.App.Storage;

namespace Kaijura.Tests;

public sealed class AutomationRuleServiceTests
{
    [Fact]
    public void NewIssueRuleMovesTicketToToDo()
    {
        var state = CreateState(Rule(
            AutomationTrigger.TicketNew,
            AutomationDestination.ToDo,
            [StatusCondition("Open")],
            location: AutomationLocation.Backlog));
        var sync = new BoardSyncService();
        var automation = new AutomationRuleService();
        var now = DateTimeOffset.Parse("2026-04-28T10:00:00Z");

        sync.Sync(state, Search(Issue("10001", "BTR-1", "Task", "Open")), now);
        var result = automation.ApplyPending(state, now);

        var issue = Assert.Single(state.Issues);
        Assert.Equal(BoardSection.Board, issue.Section);
        Assert.Equal(BoardColumn.ToDo, issue.Column);
        Assert.Single(result.Applications);
    }

    [Fact]
    public void StatusChangeRuleMovesTicketToProgress()
    {
        var state = CreateState(Rule(
            AutomationTrigger.JiraStatusChanged,
            AutomationDestination.Progress,
            [StatusCondition("In Progress")],
            location: AutomationLocation.Backlog));
        var sync = new BoardSyncService();
        var automation = new AutomationRuleService();
        var now = DateTimeOffset.Parse("2026-04-28T10:00:00Z");

        sync.Sync(state, Search(Issue("10001", "BTR-1", "Task", "Open")), now);
        automation.ApplyPending(state, now);
        sync.Sync(state, Search(Issue("10001", "BTR-1", "Task", "In Progress")), now.AddMinutes(5));
        automation.ApplyPending(state, now.AddMinutes(5));

        Assert.Equal(BoardSection.Board, state.Issues[0].Section);
        Assert.Equal(BoardColumn.Progress, state.Issues[0].Column);
    }

    [Fact]
    public void ManualMoveIsNotRevertedWithoutRelevantEvent()
    {
        var state = CreateState(Rule(
            AutomationTrigger.TicketNew,
            AutomationDestination.ToDo,
            [StatusCondition("Open")],
            location: AutomationLocation.Backlog));
        var sync = new BoardSyncService();
        var automation = new AutomationRuleService();
        var now = DateTimeOffset.Parse("2026-04-28T10:00:00Z");

        sync.Sync(state, Search(Issue("10001", "BTR-1", "Task", "Open")), now);
        automation.ApplyPending(state, now);
        sync.MoveIssue(state, "10001", BoardSection.Board, BoardColumn.Progress, ["10001"]);
        sync.Sync(state, Search(Issue("10001", "BTR-1", "Task", "Open")), now.AddMinutes(5));
        var result = automation.ApplyPending(state, now.AddMinutes(5));

        Assert.Empty(result.Applications);
        Assert.Equal(BoardColumn.Progress, state.Issues[0].Column);
    }

    [Fact]
    public void MatchingRulesRespectOrderAndStopProcessing()
    {
        var first = Rule(
            AutomationTrigger.TicketNew,
            AutomationDestination.Progress,
            [StatusCondition("Open")],
            name: "Primera");
        var second = Rule(
            AutomationTrigger.TicketNew,
            AutomationDestination.PendingQa,
            [StatusCondition("Open")],
            name: "Segunda");
        var state = CreateState(first, second);
        var sync = new BoardSyncService();
        var automation = new AutomationRuleService();
        var now = DateTimeOffset.Parse("2026-04-28T10:00:00Z");

        sync.Sync(state, Search(Issue("10001", "BTR-1", "Task", "Open")), now);
        var result = automation.ApplyPending(state, now);

        Assert.Equal(BoardColumn.Progress, state.Issues[0].Column);
        var application = Assert.Single(result.Applications);
        Assert.Equal("Primera", application.RuleName);
    }

    [Fact]
    public void InactiveRuleDoesNotRun()
    {
        var state = CreateState(Rule(
            AutomationTrigger.TicketNew,
            AutomationDestination.ToDo,
            [StatusCondition("Open")],
            enabled: false));
        var sync = new BoardSyncService();
        var automation = new AutomationRuleService();
        var now = DateTimeOffset.Parse("2026-04-28T10:00:00Z");

        sync.Sync(state, Search(Issue("10001", "BTR-1", "Task", "Open")), now);
        var result = automation.ApplyPending(state, now);

        Assert.Empty(result.Applications);
        Assert.Equal(BoardSection.Backlog, state.Issues[0].Section);
    }

    [Fact]
    public void ArchiveRuleMovesTicketAndSetsArchivedAt()
    {
        var state = CreateState(Rule(
            AutomationTrigger.TicketNew,
            AutomationDestination.Archived,
            [StatusCondition("Done")]));
        var sync = new BoardSyncService();
        var automation = new AutomationRuleService();
        var now = DateTimeOffset.Parse("2026-04-28T10:00:00Z");

        sync.Sync(state, Search(Issue("10001", "BTR-1", "Task", "Done")), now);
        automation.ApplyPending(state, now);

        Assert.Equal(BoardSection.Archived, state.Issues[0].Section);
        Assert.Equal(now, state.Issues[0].ArchivedAt);
    }

    [Fact]
    public void StatusComparisonIgnoresCaseAndOuterSpaces()
    {
        var state = CreateState(Rule(
            AutomationTrigger.TicketNew,
            AutomationDestination.ToDo,
            [StatusCondition(" in progress ")],
            location: AutomationLocation.Backlog));
        var sync = new BoardSyncService();
        var automation = new AutomationRuleService();
        var now = DateTimeOffset.Parse("2026-04-28T10:00:00Z");

        sync.Sync(state, Search(Issue("10001", "BTR-1", "Task", "In Progress")), now);
        automation.ApplyPending(state, now);

        Assert.Equal(BoardSection.Board, state.Issues[0].Section);
        Assert.Equal(BoardColumn.ToDo, state.Issues[0].Column);
    }

    [Fact]
    public void SimulationReturnsApplicationsWithoutMutatingState()
    {
        var state = CreateState();
        state.Issues.Add(new IssueState
        {
            JiraId = "10001",
            Key = "BTR-1",
            Summary = "Summary",
            JiraStatus = "Open",
            IssueType = "Task",
            Kind = IssueKind.Task,
            Section = BoardSection.Backlog,
            LastVisibleSection = BoardSection.Backlog,
            Column = BoardColumn.ToDo,
            FirstSeenAt = DateTimeOffset.Parse("2026-04-28T10:00:00Z"),
            LastSeenAt = DateTimeOffset.Parse("2026-04-28T10:00:00Z")
        });
        var automation = new AutomationRuleService();
        var rules = new[]
        {
            Rule(
                AutomationTrigger.JiraStatusChanged,
                AutomationDestination.Progress,
                [StatusCondition("Open")])
        };

        var result = automation.Simulate(state, rules, DateTimeOffset.Parse("2026-04-28T10:10:00Z"));

        Assert.Single(result.Applications);
        Assert.Equal(BoardSection.Backlog, state.Issues[0].Section);
        Assert.Equal(BoardColumn.ToDo, state.Issues[0].Column);
    }

    [Fact]
    public async Task OldConfigWithoutAutomationRulesLoadsWithEmptyRules()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"kaijura-tests-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(
                Path.Combine(directory, "state.json"),
                """
                {
                  "config": {
                    "jiraHost": "https://jira.example.local",
                    "userName": "rafa",
                    "encryptedToken": "token",
                    "jql": "project = BTR"
                  },
                  "issues": []
                }
                """);

            var store = new LocalDataStore(directory);
            var state = await store.LoadAsync(CancellationToken.None);

            Assert.Empty(state.Config.AutomationRules);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static AppState CreateState(params AutomationRule[] rules)
    {
        return new AppState
        {
            Config =
            {
                JiraHost = "https://jira.example.local",
                UserName = "rafa",
                EncryptedToken = "token",
                Jql = "project = BTR",
                TaskIssueTypes = ["Task", "Story"],
                IncidentIssueTypes = ["Bug", "Incident"],
                AutomationRules = rules.ToList()
            }
        };
    }

    private static AutomationRule Rule(
        AutomationTrigger trigger,
        AutomationDestination destination,
        List<AutomationCondition> conditions,
        bool enabled = true,
        bool stopProcessing = true,
        AutomationLocation location = AutomationLocation.Any,
        string name = "Regla")
    {
        return new AutomationRule
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            IsEnabled = enabled,
            Trigger = trigger,
            CurrentLocation = location,
            Conditions = conditions,
            Action = new AutomationAction
            {
                Destination = destination
            },
            StopProcessing = stopProcessing
        };
    }

    private static AutomationCondition StatusCondition(params string[] statuses)
    {
        return new AutomationCondition
        {
            Field = AutomationConditionField.JiraStatus,
            Operator = AutomationConditionOperator.IsAnyOf,
            Values = statuses.ToList()
        };
    }

    private static JiraSearchResult Search(params JiraIssue[] issues)
    {
        return new JiraSearchResult(issues, issues.Length, Truncated: false);
    }

    private static JiraIssue Issue(string id, string key, string type, string status)
    {
        return new JiraIssue(
            id,
            key,
            "Summary",
            status,
            type,
            $"https://jira.example.local/browse/{key}",
            DateTimeOffset.Parse("2026-04-28T10:00:00Z"));
    }
}

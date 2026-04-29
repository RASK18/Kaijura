using Kaijura.App.Models;

namespace Kaijura.App.Services;

public sealed class AutomationRuleService
{
    public AutomationRuleResult ApplyPending(AppState state, DateTimeOffset now)
    {
        return ApplyRules(
            state,
            state.Config.AutomationRules,
            issue =>
            {
                var triggers = issue.PendingAutomationTriggers.ToHashSet();
                if (state.Config.AutomationRules.Any(rule => rule.IsEnabled && rule.Trigger == AutomationTrigger.TemporalCheck))
                {
                    triggers.Add(AutomationTrigger.TemporalCheck);
                }

                return triggers;
            },
            now);
    }

    public AutomationRuleResult Simulate(AppState state, IReadOnlyList<AutomationRule> rules, DateTimeOffset now)
    {
        var simulationState = CloneStateForSimulation(state, rules);
        foreach (var issue in simulationState.Issues)
        {
            issue.PendingAutomationTriggers.Add(AutomationTrigger.TicketNew);
            issue.PendingAutomationTriggers.Add(AutomationTrigger.JiraStatusChanged);
            issue.PendingAutomationTriggers.Add(AutomationTrigger.IssueClassificationChanged);
            issue.PendingAutomationTriggers.Add(AutomationTrigger.RelevantCommentChanged);
            issue.PendingAutomationTriggers.Add(AutomationTrigger.TemporalCheck);
        }

        return ApplyRules(simulationState, simulationState.Config.AutomationRules, issue => issue.PendingAutomationTriggers, now);
    }

    private static AutomationRuleResult ApplyRules(
        AppState state,
        IReadOnlyList<AutomationRule> rules,
        Func<IssueState, IReadOnlySet<AutomationTrigger>> triggerSelector,
        DateTimeOffset now)
    {
        var activeRules = rules
            .Where(rule => rule.IsEnabled)
            .ToList();

        if (activeRules.Count == 0)
        {
            ClearPendingTriggers(state);
            return new AutomationRuleResult([]);
        }

        var applications = new List<AutomationRuleApplication>();

        foreach (var issue in state.Issues.OrderBy(issue => issue.SortOrder).ThenBy(issue => issue.Key))
        {
            if (!CanAutomate(issue))
            {
                issue.PendingAutomationTriggers.Clear();
                continue;
            }

            var triggers = triggerSelector(issue);
            if (triggers.Count == 0)
            {
                continue;
            }

            foreach (var rule in activeRules)
            {
                if (!triggers.Contains(rule.Trigger)
                    || !CanEvaluateRule(rule)
                    || !MatchesRule(issue, rule, now))
                {
                    continue;
                }

                var from = CurrentDestination(issue);
                if (from is not null && from != rule.Action.Destination)
                {
                    ApplyAction(state, issue, rule.Action.Destination, now);
                    applications.Add(new AutomationRuleApplication(
                        issue.JiraId,
                        issue.Key,
                        rule.Id,
                        rule.Name,
                        from.Value,
                        rule.Action.Destination));
                }

                if (rule.StopProcessing)
                {
                    break;
                }
            }

            issue.PendingAutomationTriggers.Clear();
        }

        NormalizeOrders(state);
        return new AutomationRuleResult(applications);
    }

    private static bool CanAutomate(IssueState issue)
    {
        return !issue.IsMissing && issue.Section is not BoardSection.Missing and not BoardSection.Archived;
    }

    private static bool CanEvaluateRule(AutomationRule rule)
    {
        return rule.Trigger != AutomationTrigger.TemporalCheck
            || rule.Conditions.Any(condition => condition.Field is AutomationConditionField.JiraUpdatedMoreThanDaysAgo
                or AutomationConditionField.FirstSeenMoreThanDaysAgo);
    }

    private static bool MatchesRule(IssueState issue, AutomationRule rule, DateTimeOffset now)
    {
        return MatchesIssueScope(issue, rule.IssueScope)
            && MatchesLocation(issue, rule.CurrentLocation)
            && rule.Conditions.All(condition => MatchesCondition(issue, condition, now));
    }

    private static bool MatchesIssueScope(IssueState issue, AutomationIssueScope scope)
    {
        return scope switch
        {
            AutomationIssueScope.Task => issue.Kind == IssueKind.Task,
            AutomationIssueScope.Incident => issue.Kind == IssueKind.Incident,
            AutomationIssueScope.Unmapped => issue.Kind == IssueKind.Unmapped,
            _ => true
        };
    }

    private static bool MatchesLocation(IssueState issue, AutomationLocation location)
    {
        return location == AutomationLocation.Any
            || location switch
            {
                AutomationLocation.Backlog => issue.Section == BoardSection.Backlog,
                AutomationLocation.ToDo => issue.Section == BoardSection.Board && issue.Column == BoardColumn.ToDo,
                AutomationLocation.Progress => issue.Section == BoardSection.Board && issue.Column == BoardColumn.Progress,
                AutomationLocation.PendingQa => issue.Section == BoardSection.Board && issue.Column == BoardColumn.PendingQa,
                AutomationLocation.ValidatedQa => issue.Section == BoardSection.Board && issue.Column == BoardColumn.ValidatedQa,
                _ => false
            };
    }

    private static bool MatchesCondition(IssueState issue, AutomationCondition condition, DateTimeOffset now)
    {
        return condition.Field switch
        {
            AutomationConditionField.JiraStatus => MatchesTextList(issue.JiraStatus, condition),
            AutomationConditionField.IssueType => MatchesTextList(issue.IssueType, condition),
            AutomationConditionField.HasUnreadComment => MatchesBool(issue.HasUnreadComment, condition),
            AutomationConditionField.JiraUpdatedMoreThanDaysAgo => MatchesAge(issue.JiraUpdatedAt, condition.Days, now),
            AutomationConditionField.FirstSeenMoreThanDaysAgo => MatchesAge(issue.FirstSeenAt, condition.Days, now),
            _ => false
        };
    }

    private static bool MatchesTextList(string value, AutomationCondition condition)
    {
        var values = CleanValues(condition.Values);
        if (values.Count == 0)
        {
            return false;
        }

        var contains = values.Contains(Normalize(value), StringComparer.OrdinalIgnoreCase);
        return condition.Operator switch
        {
            AutomationConditionOperator.IsAnyOf => contains,
            AutomationConditionOperator.IsNotAnyOf => !contains,
            _ => false
        };
    }

    private static bool MatchesBool(bool value, AutomationCondition condition)
    {
        return condition.Operator switch
        {
            AutomationConditionOperator.Is => value == condition.BoolValue,
            AutomationConditionOperator.IsNot => value != condition.BoolValue,
            _ => false
        };
    }

    private static bool MatchesAge(DateTimeOffset? value, int days, DateTimeOffset now)
    {
        return value is not null && days > 0 && now - value.Value > TimeSpan.FromDays(days);
    }

    private static void ApplyAction(AppState state, IssueState issue, AutomationDestination destination, DateTimeOffset now)
    {
        if (destination == AutomationDestination.Archived)
        {
            issue.LastVisibleSection = issue.Section;
            issue.Section = BoardSection.Archived;
            issue.IsMissing = false;
            issue.MissingSince = null;
            issue.ArchivedAt = now;
            issue.SortOrder = NextSortOrder(state, BoardSection.Archived, issue.Kind, issue.Column);
            return;
        }

        var (section, column) = ToBoardPosition(destination);
        issue.Section = section;
        issue.Column = column;
        issue.LastVisibleSection = section;
        issue.IsMissing = false;
        issue.MissingSince = null;
        issue.ArchivedAt = null;
        issue.SortOrder = NextSortOrder(state, section, issue.Kind, column);
    }

    private static (BoardSection Section, BoardColumn Column) ToBoardPosition(AutomationDestination destination)
    {
        return destination switch
        {
            AutomationDestination.Backlog => (BoardSection.Backlog, BoardColumn.ToDo),
            AutomationDestination.Progress => (BoardSection.Board, BoardColumn.Progress),
            AutomationDestination.PendingQa => (BoardSection.Board, BoardColumn.PendingQa),
            AutomationDestination.ValidatedQa => (BoardSection.Board, BoardColumn.ValidatedQa),
            _ => (BoardSection.Board, BoardColumn.ToDo)
        };
    }

    private static AutomationDestination? CurrentDestination(IssueState issue)
    {
        if (issue.Section == BoardSection.Backlog)
        {
            return AutomationDestination.Backlog;
        }

        if (issue.Section != BoardSection.Board)
        {
            return null;
        }

        return issue.Column switch
        {
            BoardColumn.Progress => AutomationDestination.Progress,
            BoardColumn.PendingQa => AutomationDestination.PendingQa,
            BoardColumn.ValidatedQa => AutomationDestination.ValidatedQa,
            _ => AutomationDestination.ToDo
        };
    }

    private static int NextSortOrder(AppState state, BoardSection section, IssueKind kind, BoardColumn column)
    {
        return state.Issues
            .Where(issue => issue.Section == section && issue.Kind == kind && issue.Column == column)
            .Select(issue => issue.SortOrder)
            .DefaultIfEmpty(-1)
            .Max() + 1;
    }

    private static void NormalizeOrders(AppState state)
    {
        foreach (var group in state.Issues.GroupBy(issue => new { issue.Section, issue.Kind, issue.Column }))
        {
            var ordered = group.OrderBy(issue => issue.SortOrder).ThenBy(issue => issue.Key).ToList();
            for (var index = 0; index < ordered.Count; index++)
            {
                ordered[index].SortOrder = index;
            }
        }
    }

    private static void ClearPendingTriggers(AppState state)
    {
        foreach (var issue in state.Issues)
        {
            issue.PendingAutomationTriggers.Clear();
        }
    }

    private static List<string> CleanValues(IEnumerable<string> values)
    {
        return values
            .Select(Normalize)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string Normalize(string value)
    {
        return value.Trim();
    }

    private static AppState CloneStateForSimulation(AppState state, IReadOnlyList<AutomationRule> rules)
    {
        return new AppState
        {
            Config = new AppConfig
            {
                AutomationRules = CloneRules(rules)
            },
            Issues = state.Issues.Select(CloneIssue).ToList(),
            LastSuccessfulSyncAt = state.LastSuccessfulSyncAt
        };
    }

    private static List<AutomationRule> CloneRules(IEnumerable<AutomationRule> rules)
    {
        return rules.Select(rule => new AutomationRule
        {
            Id = rule.Id,
            Name = rule.Name,
            IsEnabled = rule.IsEnabled,
            Trigger = rule.Trigger,
            IssueScope = rule.IssueScope,
            CurrentLocation = rule.CurrentLocation,
            Conditions = rule.Conditions.Select(condition => new AutomationCondition
            {
                Field = condition.Field,
                Operator = condition.Operator,
                Values = [.. condition.Values],
                BoolValue = condition.BoolValue,
                Days = condition.Days
            }).ToList(),
            Action = new AutomationAction
            {
                Destination = rule.Action.Destination
            },
            StopProcessing = rule.StopProcessing
        }).ToList();
    }

    private static IssueState CloneIssue(IssueState issue)
    {
        return new IssueState
        {
            JiraId = issue.JiraId,
            Key = issue.Key,
            Summary = issue.Summary,
            JiraStatus = issue.JiraStatus,
            IssueType = issue.IssueType,
            BrowseUrl = issue.BrowseUrl,
            Kind = issue.Kind,
            Section = issue.Section,
            LastVisibleSection = issue.LastVisibleSection,
            Column = issue.Column,
            SortOrder = issue.SortOrder,
            IsMissing = issue.IsMissing,
            FirstSeenAt = issue.FirstSeenAt,
            LastSeenAt = issue.LastSeenAt,
            JiraUpdatedAt = issue.JiraUpdatedAt,
            MissingSince = issue.MissingSince,
            ArchivedAt = issue.ArchivedAt,
            CommentBaselineEstablished = issue.CommentBaselineEstablished,
            HasUnreadComment = issue.HasUnreadComment,
            LastRelevantCommentId = issue.LastRelevantCommentId,
            LastReadCommentId = issue.LastReadCommentId,
            LastRelevantCommentAuthor = issue.LastRelevantCommentAuthor,
            LastRelevantCommentAt = issue.LastRelevantCommentAt
        };
    }
}

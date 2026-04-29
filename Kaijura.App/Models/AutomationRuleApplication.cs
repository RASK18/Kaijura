namespace Kaijura.App.Models;

public sealed record AutomationRuleApplication(
    string IssueId,
    string IssueKey,
    string RuleId,
    string RuleName,
    AutomationDestination From,
    AutomationDestination To);

public sealed record AutomationRuleResult(IReadOnlyList<AutomationRuleApplication> Applications);

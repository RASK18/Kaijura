namespace Kaijura.App.Models;

public sealed record JiraTransition(
    string Id,
    string Name,
    string ToStatus,
    IReadOnlyList<JiraTransitionField> Fields);

public sealed record JiraTransitionField(
    string Id,
    string Name,
    bool Required,
    string SchemaType,
    string SchemaSystem,
    string SchemaItems,
    IReadOnlyList<string> Operations);

public sealed record JiraTransitionTextField(
    string Id,
    string Name);

public sealed record JiraTransitionOption(
    string Id,
    string Name,
    string ToStatus,
    string Label,
    bool IsEnabled,
    string DisabledReason,
    bool RequiresForm,
    bool RequiresComment,
    bool RequiresWorklog,
    IReadOnlyList<JiraTransitionTextField> RequiredTextFields);

public sealed record JiraTransitionUpdate(
    string TransitionId,
    string Comment,
    string WorklogTimeSpent,
    string WorklogComment,
    DateTimeOffset? WorklogStartedAt,
    IReadOnlyDictionary<string, string> TextFields);

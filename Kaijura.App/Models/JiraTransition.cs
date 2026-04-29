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
    IReadOnlyList<string> Operations)
{
    public IReadOnlyList<JiraTransitionAllowedValue> AllowedValues { get; init; } = [];
}

public sealed record JiraTransitionAllowedValue(
    string Id,
    string Name,
    string Value);

public sealed record JiraTransitionTextField(
    string Id,
    string Name);

public sealed record JiraTransitionSelectField(
    string Id,
    string Name,
    IReadOnlyList<JiraTransitionAllowedValue> Options);

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
    IReadOnlyList<JiraTransitionTextField> RequiredTextFields,
    IReadOnlyList<JiraTransitionSelectField> RequiredSelectFields);

public sealed record JiraTransitionUpdate(
    string TransitionId,
    string Comment,
    string WorklogTimeSpent,
    string WorklogComment,
    DateTimeOffset? WorklogStartedAt,
    IReadOnlyDictionary<string, string> TextFields,
    IReadOnlyDictionary<string, JiraTransitionAllowedValue> SelectFields);

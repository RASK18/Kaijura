using Kaijura.App.Models;

namespace Kaijura.App.Services;

public sealed class JiraTransitionAnalyzer
{
    public IReadOnlyList<JiraTransitionOption> BuildOptions(IReadOnlyList<JiraTransition> transitions)
    {
        var statusCounts = transitions
            .GroupBy(TransitionStatusLabel, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        return transitions
            .Select(transition => BuildOption(transition, statusCounts[TransitionStatusLabel(transition)] > 1))
            .ToList();
    }

    private static JiraTransitionOption BuildOption(JiraTransition transition, bool hasDuplicateStatus)
    {
        var requiredFields = transition.Fields.Where(field => field.Required).ToList();
        var unsupported = new List<string>();
        var requiresComment = false;
        var requiresWorklog = false;
        var requiredTextFields = new List<JiraTransitionTextField>();
        var requiredSelectFields = new List<JiraTransitionSelectField>();

        foreach (var field in requiredFields)
        {
            if (IsCommentField(field) && SupportsOperation(field, "add"))
            {
                requiresComment = true;
                continue;
            }

            if (IsWorklogField(field) && SupportsOperation(field, "add"))
            {
                requiresWorklog = true;
                continue;
            }

            if (IsSingleSelectField(field) && SupportsOperation(field, "set"))
            {
                requiredSelectFields.Add(new JiraTransitionSelectField(field.Id, FieldLabel(field), field.AllowedValues));
                continue;
            }

            if (IsTextField(field) && SupportsOperation(field, "set"))
            {
                requiredTextFields.Add(new JiraTransitionTextField(field.Id, FieldLabel(field)));
                continue;
            }

            unsupported.Add(FieldLabel(field));
        }

        var label = LabelFor(transition, hasDuplicateStatus);
        var disabledReason = unsupported.Count == 0
            ? string.Empty
            : $"Jira exige campos no soportados: {string.Join(", ", unsupported)}.";
        var isEnabled = unsupported.Count == 0;
        var requiresForm = isEnabled && (
            requiresComment
            || requiresWorklog
            || requiredTextFields.Count > 0
            || requiredSelectFields.Count > 0);

        return new JiraTransitionOption(
            transition.Id,
            transition.Name,
            transition.ToStatus,
            label,
            isEnabled,
            disabledReason,
            requiresForm,
            requiresComment,
            requiresWorklog,
            requiredTextFields,
            requiredSelectFields);
    }

    private static string LabelFor(JiraTransition transition, bool hasDuplicateStatus)
    {
        var status = TransitionStatusLabel(transition);
        if (!hasDuplicateStatus || string.Equals(status, transition.Name, StringComparison.OrdinalIgnoreCase))
        {
            return status;
        }

        return $"{status} ({transition.Name})";
    }

    private static string TransitionStatusLabel(JiraTransition transition)
    {
        return string.IsNullOrWhiteSpace(transition.ToStatus)
            ? transition.Name
            : transition.ToStatus;
    }

    private static bool IsCommentField(JiraTransitionField field)
    {
        return EqualsAny(field.Id, "comment")
            || EqualsAny(field.Name, "comment", "comentario")
            || EqualsAny(field.SchemaSystem, "comment")
            || EqualsAny(field.SchemaType, "comment");
    }

    private static bool IsWorklogField(JiraTransitionField field)
    {
        return EqualsAny(field.Id, "worklog")
            || EqualsAny(field.SchemaSystem, "worklog")
            || EqualsAny(field.SchemaType, "worklog")
            || EqualsAny(field.SchemaItems, "worklog");
    }

    private static bool IsTextField(JiraTransitionField field)
    {
        return EqualsAny(field.SchemaType, "string", "text", "textarea")
            || field.SchemaType.Contains("string", StringComparison.OrdinalIgnoreCase)
            || field.SchemaType.Contains("text", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSingleSelectField(JiraTransitionField field)
    {
        return field.AllowedValues.Count > 0
            && !EqualsAny(field.SchemaType, "array");
    }

    private static bool SupportsOperation(JiraTransitionField field, string operation)
    {
        return field.Operations.Count == 0
            || field.Operations.Any(candidate => string.Equals(candidate, operation, StringComparison.OrdinalIgnoreCase));
    }

    private static bool EqualsAny(string value, params string[] candidates)
    {
        return candidates.Any(candidate => string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static string FieldLabel(JiraTransitionField field)
    {
        return string.IsNullOrWhiteSpace(field.Name) ? field.Id : field.Name;
    }
}

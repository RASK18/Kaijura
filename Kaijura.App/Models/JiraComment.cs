namespace Kaijura.App.Models;

public sealed record JiraComment(
    string Id,
    string AuthorName,
    string AuthorKey,
    string AuthorDisplayName,
    string AuthorEmailAddress,
    DateTimeOffset? Created,
    DateTimeOffset? Updated)
{
    public string AuthorLabel =>
        FirstNotBlank(AuthorDisplayName, AuthorName, AuthorEmailAddress, AuthorKey);

    public DateTimeOffset? LastChangedAt => Updated ?? Created;

    private static string FirstNotBlank(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }
}

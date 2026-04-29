namespace Kaijura.App.Models;

public sealed class AppConfig
{
    public string JiraHost { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string EncryptedToken { get; set; } = string.Empty;
    public string Jql { get; set; } = "ORDER BY updated DESC";
    public List<string> TaskIssueTypes { get; set; } = ["Task", "Story", "Tarea"];
    public List<string> IncidentIssueTypes { get; set; } = ["Bug", "Incident", "Incidencia"];
    public List<string> IgnoredCommentAuthors { get; set; } = [];
    public bool IgnoredCommentAuthorsSeeded { get; set; }
    public int RefreshMinutes { get; set; } = 5;
    public int MaxIssues { get; set; } = 1000;
    public string UpdateRepositoryUrl { get; set; } = "https://github.com/RASK18/Kaijura";

    public bool IsReadyForJira => !string.IsNullOrWhiteSpace(JiraHost)
        && !string.IsNullOrWhiteSpace(UserName)
        && !string.IsNullOrWhiteSpace(EncryptedToken)
        && !string.IsNullOrWhiteSpace(Jql);

    public bool SeedIgnoredCommentAuthorsWithUserName()
    {
        if (IgnoredCommentAuthorsSeeded || string.IsNullOrWhiteSpace(UserName))
        {
            return false;
        }

        var userName = UserName.Trim();
        var authors = IgnoredCommentAuthors
            .Select(author => author.Trim())
            .Where(author => !string.IsNullOrWhiteSpace(author))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!authors.Contains(userName, StringComparer.OrdinalIgnoreCase))
        {
            authors.Add(userName);
        }

        IgnoredCommentAuthors = authors;
        IgnoredCommentAuthorsSeeded = true;
        return true;
    }
}

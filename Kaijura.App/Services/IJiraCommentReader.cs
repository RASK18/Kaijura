using Kaijura.App.Models;

namespace Kaijura.App.Services;

public interface IJiraCommentReader
{
    Task<JiraComment?> GetLatestRelevantCommentAsync(
        AppConfig config,
        string token,
        string issueIdOrKey,
        IReadOnlyCollection<string> ignoredAuthors,
        CancellationToken cancellationToken);
}

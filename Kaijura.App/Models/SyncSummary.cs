namespace Kaijura.App.Models;

public sealed record SyncSummary(int TotalReturned, int VisibleCount, int MissingCount, int UnmappedCount, bool Truncated);

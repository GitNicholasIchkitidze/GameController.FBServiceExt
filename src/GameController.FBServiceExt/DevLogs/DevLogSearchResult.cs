namespace GameController.FBServiceExt.DevLogs;

public sealed record DevLogSearchResult(
    string Query,
    int Limit,
    DateTime RetrievedAtUtc,
    IReadOnlyList<DevLogEntry> Entries);
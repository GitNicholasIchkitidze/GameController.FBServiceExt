namespace GameController.FBServiceExt.DevLogs;

public sealed record DevLogEntry(
    string Timestamp,
    int? Level,
    string LevelName,
    string Source,
    string ServiceRole,
    string SourceContext,
    string RequestPath,
    string CallerTypeName,
    string CallerMemberName,
    int? CallerLineNumber,
    string Message);
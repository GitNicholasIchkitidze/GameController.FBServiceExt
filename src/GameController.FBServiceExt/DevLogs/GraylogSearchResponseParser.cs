using System.Globalization;
using System.Text.Json;

namespace GameController.FBServiceExt.DevLogs;

public static class GraylogSearchResponseParser
{
    public static IReadOnlyList<DevLogEntry> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<DevLogEntry>();
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!root.TryGetProperty("schema", out var schemaElement) || schemaElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<DevLogEntry>();
        }

        if (!root.TryGetProperty("datarows", out var dataRowsElement) || dataRowsElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<DevLogEntry>();
        }

        var fields = schemaElement
            .EnumerateArray()
            .Select(static item => item.TryGetProperty("field", out var fieldElement) && fieldElement.ValueKind == JsonValueKind.String
                ? fieldElement.GetString() ?? string.Empty
                : string.Empty)
            .ToArray();

        var entries = new List<DevLogEntry>();

        foreach (var row in dataRowsElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var values = row.EnumerateArray().ToArray();

            string GetString(string fieldName)
            {
                var index = Array.IndexOf(fields, fieldName);
                if (index < 0 || index >= values.Length)
                {
                    return string.Empty;
                }

                var element = values[index];
                return element.ValueKind switch
                {
                    JsonValueKind.Null => string.Empty,
                    JsonValueKind.String => element.GetString() ?? string.Empty,
                    JsonValueKind.Number => element.GetRawText(),
                    JsonValueKind.True => bool.TrueString,
                    JsonValueKind.False => bool.FalseString,
                    _ => element.GetRawText()
                };
            }

            int? GetLevel()
            {
                var raw = GetString("level");
                return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var level)
                    ? level
                    : null;
            }

            int? GetNullableInt(string fieldName)
            {
                var raw = GetString(fieldName);
                return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                    ? value
                    : null;
            }

            var level = GetLevel();
            entries.Add(new DevLogEntry(
                Timestamp: GetString("timestamp"),
                Level: level,
                LevelName: MapLevelName(level),
                Source: GetString("source"),
                ServiceRole: GetString("ServiceRole"),
                SourceContext: GetString("SourceContext"),
                RequestPath: GetString("RequestPath"),
                CallerTypeName: GetString("CallerTypeName"),
                CallerMemberName: GetString("CallerMemberName"),
                CallerLineNumber: GetNullableInt("CallerLineNumber"),
                Message: GetString("message")));
        }

        return entries;
    }

    public static string MapLevelName(int? level) => level switch
    {
        0 => "Emergency",
        1 => "Alert",
        2 => "Critical",
        3 => "Error",
        4 => "Warning",
        5 => "Notice",
        6 => "Info",
        7 => "Debug",
        _ => "Unknown"
    };
}
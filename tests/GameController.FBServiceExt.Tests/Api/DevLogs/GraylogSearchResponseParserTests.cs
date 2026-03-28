using GameController.FBServiceExt.DevLogs;

namespace GameController.FBServiceExt.Tests.Api.DevLogs;

public sealed class GraylogSearchResponseParserTests
{
    [Fact]
    public void Parse_MapsSchemaAndRowsToEntries()
    {
        const string json = """
        {
          "schema": [
            { "field": "timestamp" },
            { "field": "message" },
            { "field": "level" },
            { "field": "source" },
            { "field": "ServiceRole" },
            { "field": "SourceContext" },
            { "field": "RequestPath" },
            { "field": "CallerTypeName" },
            { "field": "CallerMemberName" },
            { "field": "CallerLineNumber" }
          ],
          "datarows": [
            [
              "2026-03-28T06:42:48.095Z",
              "Vote session created and moved to OptionsSent.",
              6,
              "NICK-HQ-01",
              "Worker",
              "GameController.FBServiceExt.Application.Services.NormalizedEventProcessor",
              "/api/facebook/webhooks",
              "Application.Services.NormalizedEventProcessor",
              "ProcessAsync",
              215
            ]
          ]
        }
        """;

        var entries = GraylogSearchResponseParser.Parse(json);

        var entry = Assert.Single(entries);
        Assert.Equal("2026-03-28T06:42:48.095Z", entry.Timestamp);
        Assert.Equal(6, entry.Level);
        Assert.Equal("Info", entry.LevelName);
        Assert.Equal("Worker", entry.ServiceRole);
        Assert.Equal("GameController.FBServiceExt.Application.Services.NormalizedEventProcessor", entry.SourceContext);
        Assert.Equal("Application.Services.NormalizedEventProcessor", entry.CallerTypeName);
        Assert.Equal("ProcessAsync", entry.CallerMemberName);
        Assert.Equal(215, entry.CallerLineNumber);
        Assert.Equal("Vote session created and moved to OptionsSent.", entry.Message);
        Assert.Equal("/api/facebook/webhooks", entry.RequestPath);
    }
}
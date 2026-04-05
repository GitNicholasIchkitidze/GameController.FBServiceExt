using System.Net;
using System.Text;
using GameController.FBServiceExt.DevLogs;
using GameController.FBServiceExt.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GameController.FBServiceExt.Tests.Api.DevLogs;

public sealed class GraylogLogCleanupServiceTests
{
    [Fact]
    public async Task ClearLogsAsync_CyclesIndexSetsAndDeletesOldIndices()
    {
        var options = new DevLogViewerOptions
        {
            GraylogBaseUrl = "http://graylog.test",
            Username = "admin",
            Password = "secret"
        };

        var handler = new FakeGraylogCleanupHandler();
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://graylog.test")
        };

        var service = new GraylogLogCleanupService(
            client,
            new StaticOptionsMonitor<DevLogViewerOptions>(options),
            NullLogger<GraylogLogCleanupService>.Instance);

        var summary = await service.ClearLogsAsync(CancellationToken.None);

        Assert.Equal(2, summary.IndexSetsDiscovered);
        Assert.Equal(2, summary.IndexSetsCycled);
        Assert.Equal(2, summary.DeletedIndices);
        Assert.Equal(["graylog_0", "gl-events_0"], handler.DeletedIndices);
        Assert.Contains(handler.Requests, request => request == "POST /api/system/deflector/default-set/cycle");
        Assert.Contains(handler.Requests, request => request == "POST /api/system/deflector/events-set/cycle");
    }

    private sealed class StaticOptionsMonitor<TOptions>(TOptions currentValue) : IOptionsMonitor<TOptions>
    {
        public TOptions CurrentValue { get; } = currentValue;

        public TOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<TOptions, string?> listener) => null;
    }

    private sealed class FakeGraylogCleanupHandler : HttpMessageHandler
    {
        private bool _defaultCycled;
        private bool _eventsCycled;

        public List<string> Requests { get; } = [];

        public List<string> DeletedIndices { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var pathAndQuery = request.RequestUri is null
                ? string.Empty
                : request.RequestUri.PathAndQuery;

            Requests.Add($"{request.Method.Method} {pathAndQuery}");

            return Task.FromResult(pathAndQuery switch
            {
                "/api/system/indices/index_sets?skip=0&limit=500&stats=false" => JsonResponse("""
                {
                  "index_sets": [
                    { "id": "default-set" },
                    { "id": "events-set" }
                  ]
                }
                """),
                "/api/system/deflector/default-set" => JsonResponse(_defaultCycled
                    ? "{ \"current_target\": \"graylog_1\", \"is_up\": true }"
                    : "{ \"current_target\": \"graylog_0\", \"is_up\": true }"),
                "/api/system/deflector/events-set" => JsonResponse(_eventsCycled
                    ? "{ \"current_target\": \"gl-events_1\", \"is_up\": true }"
                    : "{ \"current_target\": \"gl-events_0\", \"is_up\": true }"),
                "/api/system/indexer/indices/default-set/open" => JsonResponse("""
                {
                  "indices": [
                    { "index_name": "graylog_0" },
                    { "index_name": "graylog_1" }
                  ]
                }
                """),
                "/api/system/indexer/indices/events-set/open" => JsonResponse("""
                {
                  "indices": [
                    { "index_name": "gl-events_0" },
                    { "index_name": "gl-events_1" }
                  ]
                }
                """),
                "/api/system/indexer/indices/graylog_0" when request.Method == HttpMethod.Delete => DeleteResponse("graylog_0"),
                "/api/system/indexer/indices/gl-events_0" when request.Method == HttpMethod.Delete => DeleteResponse("gl-events_0"),
                "/api/system/deflector/default-set/cycle" when request.Method == HttpMethod.Post => CycleResponse(ref _defaultCycled),
                "/api/system/deflector/events-set/cycle" when request.Method == HttpMethod.Post => CycleResponse(ref _eventsCycled),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent($"Unhandled request: {request.Method} {pathAndQuery}", Encoding.UTF8, "text/plain")
                }
            });
        }

        private HttpResponseMessage CycleResponse(ref bool flag)
        {
            flag = true;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        private HttpResponseMessage DeleteResponse(string indexName)
        {
            DeletedIndices.Add(indexName);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        private static HttpResponseMessage JsonResponse(string json)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }
}

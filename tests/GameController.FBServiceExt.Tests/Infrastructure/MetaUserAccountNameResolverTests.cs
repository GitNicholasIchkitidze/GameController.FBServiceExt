using System.Net;
using System.Text;
using GameController.FBServiceExt.Application.Abstractions.Observability;
using GameController.FBServiceExt.Application.Abstractions.State;
using GameController.FBServiceExt.Application.Contracts.Observability;
using GameController.FBServiceExt.Application.Options;
using GameController.FBServiceExt.Infrastructure.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GameController.FBServiceExt.Tests.Infrastructure;

public sealed class MetaUserAccountNameResolverTests
{
    [Fact]
    public async Task GetOrResolveAsync_CacheHit_ReturnsCachedValueWithoutCallingGraphApi()
    {
        var store = new InMemoryUserAccountNameStore();
        await store.SetAsync("user-1", "Cached User", TimeSpan.FromDays(7), CancellationToken.None);
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("HTTP call should not happen on cache hit."));
        var resolver = CreateResolver(handler, store);

        var result = await resolver.GetOrResolveAsync("user-1", CancellationToken.None);

        Assert.Equal("Cached User", result);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task GetOrResolveAsync_CacheMiss_FetchesAndCachesUserName()
    {
        var store = new InMemoryUserAccountNameStore();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"first_name\":\"Nick\",\"last_name\":\"Chkviani\"}", Encoding.UTF8, "application/json")
        });
        var resolver = CreateResolver(handler, store);

        var firstResult = await resolver.GetOrResolveAsync("123456", CancellationToken.None);
        var secondResult = await resolver.GetOrResolveAsync("123456", CancellationToken.None);

        Assert.Equal("Nick Chkviani", firstResult);
        Assert.Equal("Nick Chkviani", secondResult);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal("Nick Chkviani", await store.GetAsync("123456", CancellationToken.None));
    }

    [Fact]
    public async Task GetOrResolveAsync_SimulatorUser_ReturnsUserIdWithoutCallingGraphApi()
    {
        var store = new InMemoryUserAccountNameStore();
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("HTTP call should not happen for simulator users."));
        var resolver = CreateResolver(handler, store, new MetaMessengerOptions
        {
            PageAccessToken = "token-1",
            GraphApiVersion = "v24.0",
            GraphApiBaseUrl = "http://127.0.0.1:5290",
            UserAccountNameCacheTtl = TimeSpan.FromDays(7)
        });

        var result = await resolver.GetOrResolveAsync("simulate-user-000031", CancellationToken.None);

        Assert.Equal("simulate-user-000031", result);
        Assert.Equal(0, handler.CallCount);
        Assert.Equal("simulate-user-000031", await store.GetAsync("simulate-user-000031", CancellationToken.None));
    }

    [Fact]
    public async Task GetOrResolveAsync_FetchFailure_CachesFallbackValue()
    {
        var store = new InMemoryUserAccountNameStore();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        var resolver = CreateResolver(handler, store);

        var firstResult = await resolver.GetOrResolveAsync("999888", CancellationToken.None);
        var secondResult = await resolver.GetOrResolveAsync("999888", CancellationToken.None);

        Assert.Equal("999888 NOT FETCHED FROM FB", firstResult);
        Assert.Equal("999888 NOT FETCHED FROM FB", secondResult);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal("999888 NOT FETCHED FROM FB", await store.GetAsync("999888", CancellationToken.None));
    }

    private static MetaUserAccountNameResolver CreateResolver(HttpMessageHandler handler, IUserAccountNameStore store, MetaMessengerOptions? options = null)
    {
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://graph.facebook.com")
        };

        return new MetaUserAccountNameResolver(
            client,
            store,
            new TestOptionsMonitor(options ?? new MetaMessengerOptions
            {
                PageAccessToken = "token-1",
                GraphApiVersion = "v24.0",
                GraphApiBaseUrl = "https://graph.facebook.com",
                UserAccountNameCacheTtl = TimeSpan.FromDays(7)
            }),
            new TestRuntimeMetricsCollector(),
            NullLogger<MetaUserAccountNameResolver>.Instance);
    }

    private sealed class InMemoryUserAccountNameStore : IUserAccountNameStore
    {
        private readonly Dictionary<string, string> _items = new(StringComparer.OrdinalIgnoreCase);

        public ValueTask<string?> GetAsync(string userId, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return ValueTask.FromResult<string?>(_items.TryGetValue(userId, out var value) ? value : null);
        }

        public ValueTask SetAsync(string userId, string accountName, TimeSpan retention, CancellationToken cancellationToken)
        {
            _ = retention;
            _ = cancellationToken;
            _items[userId] = accountName;
            return ValueTask.CompletedTask;
        }

        public ValueTask RemoveAsync(string userId, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            _items.Remove(userId);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            CallCount++;
            return Task.FromResult(_handler(request));
        }
    }

    private sealed class TestOptionsMonitor : IOptionsMonitor<MetaMessengerOptions>
    {
        public TestOptionsMonitor(MetaMessengerOptions currentValue)
        {
            CurrentValue = currentValue;
        }

        public MetaMessengerOptions CurrentValue { get; }

        public MetaMessengerOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<MetaMessengerOptions, string?> listener)
        {
            _ = listener;
            return null;
        }
    }

    private sealed class TestRuntimeMetricsCollector : IRuntimeMetricsCollector
    {
        public void Increment(string metricName, long value = 1) { }
        public void ObserveDuration(string metricName, double milliseconds) { }
        public void ObserveValue(string metricName, double value) { }
        public void SetGauge(string metricName, double value) { }

        public RuntimeMetricsSnapshot CreateSnapshot() => new(
            ServiceRole: "Test",
            InstanceId: "test-instance",
            MachineName: "test-machine",
            EnvironmentName: "Test",
            ProcessId: 0,
            UpdatedAtUtc: DateTime.UtcNow,
            Counters: new Dictionary<string, long>(),
            Gauges: new Dictionary<string, double>(),
            Distributions: new Dictionary<string, MetricDistributionSnapshot>());
    }
}

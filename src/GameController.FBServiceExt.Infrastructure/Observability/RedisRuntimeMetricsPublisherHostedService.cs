using System.Text.Json;
using GameController.FBServiceExt.Application.Abstractions.Observability;
using GameController.FBServiceExt.Infrastructure.Options;
using GameController.FBServiceExt.Infrastructure.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameController.FBServiceExt.Infrastructure.Observability;

internal sealed class RedisRuntimeMetricsPublisherHostedService : BackgroundService
{
    private readonly RedisConnectionProvider _redisConnectionProvider;
    private readonly IRuntimeMetricsCollector _runtimeMetricsCollector;
    private readonly IOptionsMonitor<RuntimeMetricsOptions> _optionsMonitor;
    private readonly IOptionsMonitor<RedisOptions> _redisOptionsMonitor;
    private readonly ILogger<RedisRuntimeMetricsPublisherHostedService> _logger;

    public RedisRuntimeMetricsPublisherHostedService(
        RedisConnectionProvider redisConnectionProvider,
        IRuntimeMetricsCollector runtimeMetricsCollector,
        IOptionsMonitor<RuntimeMetricsOptions> optionsMonitor,
        IOptionsMonitor<RedisOptions> redisOptionsMonitor,
        ILogger<RedisRuntimeMetricsPublisherHostedService> logger)
    {
        _redisConnectionProvider = redisConnectionProvider;
        _runtimeMetricsCollector = runtimeMetricsCollector;
        _optionsMonitor = optionsMonitor;
        _redisOptionsMonitor = redisOptionsMonitor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PublishSnapshotAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Runtime metrics snapshot publish failed.");
            }

            var interval = Math.Max(500, _optionsMonitor.CurrentValue.FlushIntervalMilliseconds);
            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task PublishSnapshotAsync(CancellationToken cancellationToken)
    {
        var snapshot = _runtimeMetricsCollector.CreateSnapshot();
        var db = await _redisConnectionProvider.GetDatabaseAsync(cancellationToken);
        var redisOptions = _redisOptionsMonitor.CurrentValue;
        var metricsOptions = _optionsMonitor.CurrentValue;
        var snapshotKey = $"{redisOptions.KeyPrefix}:metrics:{snapshot.ServiceRole.ToLowerInvariant()}:{snapshot.InstanceId}";
        var indexKey = $"{redisOptions.KeyPrefix}:metrics:index";
        var ttl = TimeSpan.FromSeconds(Math.Max(5, metricsOptions.SnapshotTtlSeconds));
        var payload = JsonSerializer.Serialize(snapshot);

        await db.StringSetAsync(snapshotKey, payload, ttl);
        await db.SetAddAsync(indexKey, snapshotKey);
        await db.KeyExpireAsync(indexKey, TimeSpan.FromDays(7));
    }
}

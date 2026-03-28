using GameController.FBServiceExt.Infrastructure.State;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace GameController.FBServiceExt.Infrastructure.HealthChecks;

internal sealed class RedisConfigurationHealthCheck : IHealthCheck
{
    private readonly RedisConnectionProvider _connectionProvider;

    public RedisConfigurationHealthCheck(RedisConnectionProvider connectionProvider)
    {
        _connectionProvider = connectionProvider;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var database = await _connectionProvider.GetDatabaseAsync(cancellationToken);
            var ping = await database.PingAsync();
            return HealthCheckResult.Healthy($"Redis ping succeeded in {ping.TotalMilliseconds:N0} ms.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Redis connection failed.", ex);
        }
    }
}

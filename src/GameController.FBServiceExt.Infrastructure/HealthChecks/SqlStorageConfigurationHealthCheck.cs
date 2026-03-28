using GameController.FBServiceExt.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace GameController.FBServiceExt.Infrastructure.HealthChecks;

internal sealed class SqlStorageConfigurationHealthCheck : IHealthCheck
{
    private readonly IDbContextFactory<FbServiceExtDbContext> _dbContextFactory;

    public SqlStorageConfigurationHealthCheck(IDbContextFactory<FbServiceExtDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        _ = context;

        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            var canConnect = await dbContext.Database.CanConnectAsync(cancellationToken);
            return canConnect
                ? HealthCheckResult.Healthy("SQL storage is reachable.")
                : HealthCheckResult.Unhealthy("SQL storage is unavailable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("SQL storage is unavailable.", ex);
        }
    }
}

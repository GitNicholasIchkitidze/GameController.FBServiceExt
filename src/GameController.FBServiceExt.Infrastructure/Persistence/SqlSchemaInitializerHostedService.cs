using GameController.FBServiceExt.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameController.FBServiceExt.Infrastructure.Persistence;

internal sealed class SqlSchemaInitializerHostedService : IHostedService
{
    private readonly IDbContextFactory<FbServiceExtDbContext> _dbContextFactory;
    private readonly ILogger<SqlSchemaInitializerHostedService> _logger;

    public SqlSchemaInitializerHostedService(
        IDbContextFactory<FbServiceExtDbContext> dbContextFactory,
        ILogger<SqlSchemaInitializerHostedService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);
        _logger.LogInformation("SQL storage schema ensured through EF Core EnsureCreated.");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Task.CompletedTask;
    }
}

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
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            IF COL_LENGTH('dbo.AcceptedVotes', 'UserAccountName') IS NULL
            BEGIN
                ALTER TABLE dbo.AcceptedVotes ADD UserAccountName NVARCHAR(400) NULL;
            END

            IF COL_LENGTH('dbo.AcceptedVotes', 'ShowId') IS NULL
            BEGIN
                ALTER TABLE dbo.AcceptedVotes ADD ShowId NVARCHAR(200) NULL;
                UPDATE dbo.AcceptedVotes SET ShowId = '' WHERE ShowId IS NULL;
                ALTER TABLE dbo.AcceptedVotes ALTER COLUMN ShowId NVARCHAR(200) NOT NULL;
                CREATE INDEX IX_AcceptedVotes_ShowId_ConfirmedAtUtc ON dbo.AcceptedVotes (ShowId, ConfirmedAtUtc);
            END
            """,
            cancellationToken);
        _logger.LogInformation("SQL storage schema ensured through EF Core EnsureCreated and idempotent column checks.");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Task.CompletedTask;
    }
}

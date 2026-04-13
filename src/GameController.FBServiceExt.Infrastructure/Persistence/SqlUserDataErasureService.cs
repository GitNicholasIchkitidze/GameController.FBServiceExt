using GameController.FBServiceExt.Application.Abstractions.Persistence;
using GameController.FBServiceExt.Application.Contracts.Persistence;
using GameController.FBServiceExt.Infrastructure.Data;
using GameController.FBServiceExt.Infrastructure.Options;
using GameController.FBServiceExt.Infrastructure.State;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace GameController.FBServiceExt.Infrastructure.Persistence;

internal sealed class SqlUserDataErasureService : IUserDataErasureService
{
    private readonly IDbContextFactory<FbServiceExtDbContext> _dbContextFactory;
    private readonly RedisConnectionProvider _redisConnectionProvider;
    private readonly IOptionsMonitor<RedisOptions> _redisOptionsMonitor;

    public SqlUserDataErasureService(
        IDbContextFactory<FbServiceExtDbContext> dbContextFactory,
        RedisConnectionProvider redisConnectionProvider,
        IOptionsMonitor<RedisOptions> redisOptionsMonitor)
    {
        _dbContextFactory = dbContextFactory;
        _redisConnectionProvider = redisConnectionProvider;
        _redisOptionsMonitor = redisOptionsMonitor;
    }

    // forget-me flow-ისას მომხმარებლის SQL მონაცემებს შლის: AcceptedVotes და NormalizedEvents.
    // შემდეგ შესაბამის Redis state-საც ასუფთავებს.
    public async ValueTask<UserDataErasureResult> EraseUserDataAsync(string userId, CancellationToken cancellationToken)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var executionStrategy = dbContext.Database.CreateExecutionStrategy();

        return await executionStrategy.ExecuteAsync(async () =>
        {
            await using var transactionalContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await transactionalContext.Database.BeginTransactionAsync(cancellationToken);

            var eventIds = await transactionalContext.NormalizedEvents
                .Where(item => item.SenderId == userId)
                .Select(item => item.EventId)
                .ToArrayAsync(cancellationToken);

            var normalizedEventsDeleted = await transactionalContext.NormalizedEvents
                .Where(item => item.SenderId == userId)
                .ExecuteDeleteAsync(cancellationToken);

            var acceptedVotesDeleted = await transactionalContext.AcceptedVotes
                .Where(item => item.UserId == userId)
                .ExecuteDeleteAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            await DeleteRedisUserStateAsync(userId, eventIds, cancellationToken);

            return new UserDataErasureResult(
                NormalizedEventsDeleted: normalizedEventsDeleted,
                AcceptedVotesDeleted: acceptedVotesDeleted);
        });
    }

    // SQL erase-ის შემდეგ მომხმარებელთან დაკავშირებულ Redis cache/dedupe/cooldown key-ებს შლის.
    private async Task DeleteRedisUserStateAsync(string userId, IReadOnlyCollection<string> eventIds, CancellationToken cancellationToken)
    {
        var connection = await _redisConnectionProvider.GetConnectionAsync(cancellationToken);
        var database = connection.GetDatabase();
        var prefix = _redisOptionsMonitor.CurrentValue.KeyPrefix;
        var keys = new HashSet<string>(StringComparer.Ordinal);

        keys.Add(RedisKeyFactory.UserAccountName(prefix, userId));
        foreach (var eventId in eventIds)
        {
            if (!string.IsNullOrWhiteSpace(eventId))
            {
                keys.Add(RedisKeyFactory.ProcessedEvent(prefix, eventId));
            }
        }

        foreach (var endpoint in connection.GetEndPoints())
        {
            var server = connection.GetServer(endpoint);
            if (!server.IsConnected || server.IsReplica)
            {
                continue;
            }

            CollectKeys(server, keys, $"{prefix}:vote:cooldown:*:*:{userId}");
            CollectKeys(server, keys, $"{prefix}:vote:session:*:{userId}");
        }

        if (keys.Count == 0)
        {
            return;
        }

        var redisKeys = keys.Select(key => (RedisKey)key).ToArray();
        await database.KeyDeleteAsync(redisKeys);
    }

    private static void CollectKeys(IServer server, ISet<string> destination, string pattern)
    {
        foreach (var key in server.Keys(pattern: pattern, pageSize: 500))
        {
            destination.Add(key.ToString());
        }
    }
}

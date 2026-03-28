using GameController.FBServiceExt.Application.Abstractions.State;
using Microsoft.Extensions.Options;

namespace GameController.FBServiceExt.Infrastructure.State;

internal sealed class RedisUserProcessingLockManager : IUserProcessingLockManager
{
    private readonly RedisConnectionProvider _connectionProvider;
    private readonly IOptionsMonitor<Options.RedisOptions> _optionsMonitor;

    public RedisUserProcessingLockManager(
        RedisConnectionProvider connectionProvider,
        IOptionsMonitor<Options.RedisOptions> optionsMonitor)
    {
        _connectionProvider = connectionProvider;
        _optionsMonitor = optionsMonitor;
    }

    public async ValueTask<IDistributedLockHandle?> TryAcquireAsync(string scope, TimeSpan ttl, CancellationToken cancellationToken)
    {
        var database = await _connectionProvider.GetDatabaseAsync(cancellationToken);
        var key = RedisKeyFactory.UserLock(_optionsMonitor.CurrentValue.KeyPrefix, scope);
        var token = Guid.NewGuid().ToString("N");

        var acquired = await database.LockTakeAsync(key, token, ttl);
        return acquired
            ? new RedisDistributedLockHandle(database, key, token)
            : null;
    }
}

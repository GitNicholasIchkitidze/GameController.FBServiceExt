using GameController.FBServiceExt.Application.Abstractions.State;
using StackExchange.Redis;

namespace GameController.FBServiceExt.Infrastructure.State;

internal sealed class RedisDistributedLockHandle : IDistributedLockHandle
{
    private readonly IDatabase _database;
    private readonly RedisKey _key;
    private readonly RedisValue _token;

    public RedisDistributedLockHandle(IDatabase database, RedisKey key, RedisValue token)
    {
        _database = database;
        _key = key;
        _token = token;
    }

    public async ValueTask DisposeAsync()
    {
        await _database.LockReleaseAsync(_key, _token);
    }
}

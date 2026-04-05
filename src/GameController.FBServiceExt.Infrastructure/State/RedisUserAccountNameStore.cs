using GameController.FBServiceExt.Application.Abstractions.State;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace GameController.FBServiceExt.Infrastructure.State;

internal sealed class RedisUserAccountNameStore : IUserAccountNameStore
{
    private readonly RedisConnectionProvider _connectionProvider;
    private readonly IOptionsMonitor<Options.RedisOptions> _optionsMonitor;

    public RedisUserAccountNameStore(
        RedisConnectionProvider connectionProvider,
        IOptionsMonitor<Options.RedisOptions> optionsMonitor)
    {
        _connectionProvider = connectionProvider;
        _optionsMonitor = optionsMonitor;
    }

    public async ValueTask<string?> GetAsync(string userId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        var database = await _connectionProvider.GetDatabaseAsync(cancellationToken);
        var key = RedisKeyFactory.UserAccountName(_optionsMonitor.CurrentValue.KeyPrefix, userId);
        var value = await database.StringGetAsync(key);
        return value.IsNullOrEmpty ? null : value.ToString();
    }

    public async ValueTask SetAsync(string userId, string accountName, TimeSpan retention, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(accountName))
        {
            return;
        }

        var database = await _connectionProvider.GetDatabaseAsync(cancellationToken);
        var key = RedisKeyFactory.UserAccountName(_optionsMonitor.CurrentValue.KeyPrefix, userId);
        await database.StringSetAsync(key, accountName, retention, when: When.Always);
    }

    public async ValueTask RemoveAsync(string userId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        var database = await _connectionProvider.GetDatabaseAsync(cancellationToken);
        var key = RedisKeyFactory.UserAccountName(_optionsMonitor.CurrentValue.KeyPrefix, userId);
        await database.KeyDeleteAsync(key);
    }
}

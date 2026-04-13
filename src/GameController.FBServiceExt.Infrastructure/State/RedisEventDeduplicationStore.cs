using GameController.FBServiceExt.Application.Abstractions.State;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace GameController.FBServiceExt.Infrastructure.State;

internal sealed class RedisEventDeduplicationStore : IEventDeduplicationStore
{
    private readonly RedisConnectionProvider _connectionProvider;
    private readonly IOptionsMonitor<Options.RedisOptions> _optionsMonitor;

    public RedisEventDeduplicationStore(
        RedisConnectionProvider connectionProvider,
        IOptionsMonitor<Options.RedisOptions> optionsMonitor)
    {
        _connectionProvider = connectionProvider;
        _optionsMonitor = optionsMonitor;
    }

    // ამოწმებს იგივე event უკვე დამუშავებულია თუ არა, რათა duplicate processing ავიცილოთ.
    public async ValueTask<bool> IsProcessedAsync(string eventId, CancellationToken cancellationToken)
    {
        var database = await _connectionProvider.GetDatabaseAsync(cancellationToken);
        var key = RedisKeyFactory.ProcessedEvent(_optionsMonitor.CurrentValue.KeyPrefix, eventId);
        return await database.KeyExistsAsync(key);
    }

    // წარმატებით დამუშავებულ event-ს dedupe key-ად ინახავს Redis-ში.
    public async ValueTask MarkProcessedAsync(string eventId, TimeSpan retention, CancellationToken cancellationToken)
    {
        var database = await _connectionProvider.GetDatabaseAsync(cancellationToken);
        var key = RedisKeyFactory.ProcessedEvent(_optionsMonitor.CurrentValue.KeyPrefix, eventId);
        await database.StringSetAsync(key, "1", retention, when: When.Always);
    }
}

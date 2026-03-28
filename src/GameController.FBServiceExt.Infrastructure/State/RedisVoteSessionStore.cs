using System.Text.Json;
using GameController.FBServiceExt.Application.Abstractions.State;
using GameController.FBServiceExt.Application.Contracts.Runtime;
using Microsoft.Extensions.Options;

namespace GameController.FBServiceExt.Infrastructure.State;

internal sealed class RedisVoteSessionStore : IVoteSessionStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.General);
    private readonly RedisConnectionProvider _connectionProvider;
    private readonly IOptionsMonitor<Options.RedisOptions> _optionsMonitor;
    private readonly TimeProvider _timeProvider;

    public RedisVoteSessionStore(
        RedisConnectionProvider connectionProvider,
        IOptionsMonitor<Options.RedisOptions> optionsMonitor,
        TimeProvider timeProvider)
    {
        _connectionProvider = connectionProvider;
        _optionsMonitor = optionsMonitor;
        _timeProvider = timeProvider;
    }

    public async ValueTask<VoteSessionSnapshot?> GetAsync(string userId, string recipientId, CancellationToken cancellationToken)
    {
        var database = await _connectionProvider.GetDatabaseAsync(cancellationToken);
        var key = RedisKeyFactory.VoteSession(_optionsMonitor.CurrentValue.KeyPrefix, recipientId, userId);
        var value = await database.StringGetAsync(key);
        if (!value.HasValue)
        {
            return null;
        }

        return JsonSerializer.Deserialize<VoteSessionSnapshot>(value!, SerializerOptions);
    }

    public async ValueTask SaveAsync(VoteSessionSnapshot snapshot, CancellationToken cancellationToken)
    {
        var database = await _connectionProvider.GetDatabaseAsync(cancellationToken);
        var key = RedisKeyFactory.VoteSession(_optionsMonitor.CurrentValue.KeyPrefix, snapshot.RecipientId, snapshot.UserId);
        var payload = JsonSerializer.Serialize(snapshot, SerializerOptions);
        var ttl = CalculateTtl(snapshot);

        if (ttl <= TimeSpan.Zero)
        {
            await database.KeyDeleteAsync(key);
            return;
        }

        await database.StringSetAsync(key, payload, ttl);
    }

    public async ValueTask RemoveAsync(string userId, string recipientId, CancellationToken cancellationToken)
    {
        var database = await _connectionProvider.GetDatabaseAsync(cancellationToken);
        var key = RedisKeyFactory.VoteSession(_optionsMonitor.CurrentValue.KeyPrefix, recipientId, userId);
        await database.KeyDeleteAsync(key);
    }

    private TimeSpan CalculateTtl(VoteSessionSnapshot snapshot)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (snapshot.ExpiresAtUtc.HasValue)
        {
            return snapshot.ExpiresAtUtc.Value - now;
        }

        if (snapshot.CooldownUntilUtc.HasValue)
        {
            return snapshot.CooldownUntilUtc.Value - now;
        }

        return TimeSpan.Zero;
    }
}

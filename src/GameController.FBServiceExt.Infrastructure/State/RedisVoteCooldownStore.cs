using GameController.FBServiceExt.Application.Abstractions.State;
using GameController.FBServiceExt.Application.Contracts.Runtime;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace GameController.FBServiceExt.Infrastructure.State;

internal sealed class RedisVoteCooldownStore : IVoteCooldownStore
{
    private readonly RedisConnectionProvider _connectionProvider;
    private readonly IOptionsMonitor<Options.RedisOptions> _optionsMonitor;

    public RedisVoteCooldownStore(
        RedisConnectionProvider connectionProvider,
        IOptionsMonitor<Options.RedisOptions> optionsMonitor)
    {
        _connectionProvider = connectionProvider;
        _optionsMonitor = optionsMonitor;
    }

    // კონკრეტული user/show-ის cooldown snapshot-ს Redis-იდან კითხულობს.
    public async ValueTask<VoteCooldownSnapshot?> GetAsync(string showId, string userId, string recipientId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(showId) || string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(recipientId))
        {
            return null;
        }

        var database = await _connectionProvider.GetDatabaseAsync(cancellationToken);
        var key = RedisKeyFactory.VoteCooldown(_optionsMonitor.CurrentValue.KeyPrefix, showId, recipientId, userId);
        var value = await database.StringGetAsync(key);
        if (value.IsNullOrEmpty || !DateTime.TryParse(value.ToString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var lastVotedUtc))
        {
            return null;
        }

        return new VoteCooldownSnapshot(showId, userId, recipientId, DateTime.SpecifyKind(lastVotedUtc, DateTimeKind.Utc));
    }

    // მიღებული ხმის შემდეგ cooldown state-ს Redis-ში ინახავს TTL-ით.
    public async ValueTask SaveAsync(VoteCooldownSnapshot snapshot, TimeSpan retention, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(snapshot.ShowId) || string.IsNullOrWhiteSpace(snapshot.UserId) || string.IsNullOrWhiteSpace(snapshot.RecipientId) || retention <= TimeSpan.Zero)
        {
            return;
        }

        var database = await _connectionProvider.GetDatabaseAsync(cancellationToken);
        var key = RedisKeyFactory.VoteCooldown(_optionsMonitor.CurrentValue.KeyPrefix, snapshot.ShowId, snapshot.RecipientId, snapshot.UserId);
        await database.StringSetAsync(key, snapshot.LastVotedUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture), retention, when: When.Always);
    }

    // ვადაგასული cooldown key-ს Redis-იდან შლის.
    public async ValueTask RemoveAsync(string showId, string userId, string recipientId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(showId) || string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(recipientId))
        {
            return;
        }

        var database = await _connectionProvider.GetDatabaseAsync(cancellationToken);
        var key = RedisKeyFactory.VoteCooldown(_optionsMonitor.CurrentValue.KeyPrefix, showId, recipientId, userId);
        await database.KeyDeleteAsync(key);
    }
}

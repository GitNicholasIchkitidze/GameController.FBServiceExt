using System.Text.Json;
using GameController.FBServiceExt.Application.Abstractions.State;
using GameController.FBServiceExt.Application.Contracts.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace GameController.FBServiceExt.Infrastructure.State;

internal sealed class RedisVotingGateService : IVotingGateService, IDisposable
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly RedisConnectionProvider _connectionProvider;
    private readonly IOptionsMonitor<Options.RedisOptions> _optionsMonitor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RedisVotingGateService> _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _subscriptionGate = new(1, 1);

    private CacheSnapshot _snapshot = new(new VotingRuntimeState(true, null), DateTime.MinValue);
    private bool _subscriptionStarted;
    private ISubscriber? _subscriber;
    private RedisChannel _subscriptionChannel;

    public RedisVotingGateService(
        RedisConnectionProvider connectionProvider,
        IOptionsMonitor<Options.RedisOptions> optionsMonitor,
        TimeProvider timeProvider,
        ILogger<RedisVotingGateService> logger)
    {
        _connectionProvider = connectionProvider;
        _optionsMonitor = optionsMonitor;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    // VotingStarted და ActiveShowId-ს Redis-იდან კითხულობს.
    // აქვს მოკლე cache და pub/sub, რათა ყველა instance ერთ runtime state-ზე შეთანხმდეს.
    public async ValueTask<VotingRuntimeState> GetStateAsync(CancellationToken cancellationToken)
    {
        await EnsureSubscriptionAsync(cancellationToken);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var cached = _snapshot;
        if (cached.ExpiresAtUtc > now)
        {
            return cached.State;
        }

        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            now = _timeProvider.GetUtcNow().UtcDateTime;
            cached = _snapshot;
            if (cached.ExpiresAtUtc > now)
            {
                return cached.State;
            }

            var database = await _connectionProvider.GetDatabaseAsync(cancellationToken);
            var prefix = _optionsMonitor.CurrentValue.KeyPrefix;
            var startedValue = await database.StringGetAsync(RedisKeyFactory.VotingStarted(prefix));
            var activeShowIdValue = await database.StringGetAsync(RedisKeyFactory.ActiveShowId(prefix));
            var state = new VotingRuntimeState(
                VotingStarted: startedValue.IsNullOrEmpty ? true : ParseBoolean(startedValue),
                ActiveShowId: activeShowIdValue.IsNullOrEmpty ? null : activeShowIdValue.ToString());

            _snapshot = new CacheSnapshot(state, now.Add(CacheTtl));
            return state;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Voting runtime state lookup failed. Falling back to the cached/default value.");
            return _snapshot.State;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    // voting runtime state-ს Redis-ში წერს და ცვლილებას pub/sub-ით სხვა instance-ებს ატყობინებს.
    public async ValueTask SetStateAsync(VotingRuntimeState state, CancellationToken cancellationToken)
    {
        await EnsureSubscriptionAsync(cancellationToken);

        var database = await _connectionProvider.GetDatabaseAsync(cancellationToken);
        var prefix = _optionsMonitor.CurrentValue.KeyPrefix;
        await database.StringSetAsync(RedisKeyFactory.VotingStarted(prefix), state.VotingStarted ? "1" : "0", when: When.Always);

        var activeShowIdKey = RedisKeyFactory.ActiveShowId(prefix);
        if (string.IsNullOrWhiteSpace(state.ActiveShowId))
        {
            await database.KeyDeleteAsync(activeShowIdKey);
        }
        else
        {
            await database.StringSetAsync(activeShowIdKey, state.ActiveShowId.Trim(), when: When.Always);
        }

        UpdateSnapshot(state);

        try
        {
            var subscriber = _subscriber ?? (await _connectionProvider.GetConnectionAsync(cancellationToken)).GetSubscriber();
            var payload = JsonSerializer.Serialize(new StateMessage(state.VotingStarted, state.ActiveShowId), SerializerOptions);
            await subscriber.PublishAsync(RedisChannel.Literal(RedisKeyFactory.VotingStateChangedChannel(prefix)), payload);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to publish voting runtime state change notification. Other instances will observe the change on the next Redis refresh.");
        }
    }

    public async ValueTask<bool> IsVotingStartedAsync(CancellationToken cancellationToken)
        => (await GetStateAsync(cancellationToken)).VotingStarted;

    public async ValueTask SetVotingStartedAsync(bool started, CancellationToken cancellationToken)
    {
        var state = await GetStateAsync(cancellationToken);
        await SetStateAsync(state with { VotingStarted = started }, cancellationToken);
    }

    public async ValueTask<string?> GetActiveShowIdAsync(CancellationToken cancellationToken)
        => (await GetStateAsync(cancellationToken)).ActiveShowId;

    public async ValueTask SetActiveShowIdAsync(string? activeShowId, CancellationToken cancellationToken)
    {
        var state = await GetStateAsync(cancellationToken);
        await SetStateAsync(state with { ActiveShowId = string.IsNullOrWhiteSpace(activeShowId) ? null : activeShowId.Trim() }, cancellationToken);
    }

    public void Dispose()
    {
        try
        {
            if (_subscriber is not null && _subscriptionStarted)
            {
                _subscriber.Unsubscribe(_subscriptionChannel);
            }
        }
        catch
        {
        }
        finally
        {
            _refreshGate.Dispose();
            _subscriptionGate.Dispose();
        }
    }

    // voting state change channel-ზე subscription-ს აყენებს, რომ local cache სწრაფად განახლდეს.
    private async ValueTask EnsureSubscriptionAsync(CancellationToken cancellationToken)
    {
        if (_subscriptionStarted)
        {
            return;
        }

        await _subscriptionGate.WaitAsync(cancellationToken);
        try
        {
            if (_subscriptionStarted)
            {
                return;
            }

            var prefix = _optionsMonitor.CurrentValue.KeyPrefix;
            var connection = await _connectionProvider.GetConnectionAsync(cancellationToken);
            _subscriber = connection.GetSubscriber();
            _subscriptionChannel = RedisChannel.Literal(RedisKeyFactory.VotingStateChangedChannel(prefix));

            await _subscriber.SubscribeAsync(_subscriptionChannel, (_, value) =>
            {
                if (value.IsNullOrEmpty)
                {
                    return;
                }

                try
                {
                    var message = JsonSerializer.Deserialize<StateMessage>(value!, SerializerOptions);
                    if (message is null)
                    {
                        return;
                    }

                    UpdateSnapshot(new VotingRuntimeState(message.VotingStarted, message.ActiveShowId));
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Failed to parse voting runtime state notification. Channel: {Channel}", _subscriptionChannel.ToString());
                }
            });

            _subscriptionStarted = true;
            _logger.LogInformation("Subscribed to Redis voting runtime state change notifications. Channel: {Channel}", _subscriptionChannel.ToString());
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to subscribe to voting runtime state change notifications. Falling back to periodic Redis refresh.");
        }
        finally
        {
            _subscriptionGate.Release();
        }
    }

    private void UpdateSnapshot(VotingRuntimeState state)
    {
        _snapshot = new CacheSnapshot(state, _timeProvider.GetUtcNow().UtcDateTime.Add(CacheTtl));
    }

    private static bool ParseBoolean(RedisValue value)
    {
        if (value.IsNullOrEmpty)
        {
            return true;
        }

        var text = value.ToString();
        return text.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record CacheSnapshot(VotingRuntimeState State, DateTime ExpiresAtUtc);

    private sealed record StateMessage(bool VotingStarted, string? ActiveShowId);
}

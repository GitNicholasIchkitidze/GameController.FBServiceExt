using System.Text.Json;
using GameController.FBServiceExt.Application.Abstractions.Observability;
using GameController.FBServiceExt.Application.Contracts.Observability;
using GameController.FBServiceExt.Infrastructure.Options;
using GameController.FBServiceExt.Infrastructure.State;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace GameController.FBServiceExt.Infrastructure.Observability;

internal sealed class RedisRuntimeMetricsSnapshotReader : IRuntimeMetricsSnapshotReader
{
    private readonly RedisConnectionProvider _redisConnectionProvider;
    private readonly IOptionsMonitor<RedisOptions> _redisOptionsMonitor;

    public RedisRuntimeMetricsSnapshotReader(
        RedisConnectionProvider redisConnectionProvider,
        IOptionsMonitor<RedisOptions> redisOptionsMonitor)
    {
        _redisConnectionProvider = redisConnectionProvider;
        _redisOptionsMonitor = redisOptionsMonitor;
    }

    // Redis-ში გამოქვეყნებულ ყველა ცოცხალ metrics snapshot-ს კითხულობს dashboard-ებისთვის.
    public async ValueTask<IReadOnlyList<RuntimeMetricsSnapshot>> ListSnapshotsAsync(CancellationToken cancellationToken)
    {
        var db = await _redisConnectionProvider.GetDatabaseAsync(cancellationToken);
        var indexKey = $"{_redisOptionsMonitor.CurrentValue.KeyPrefix}:metrics:index";
        var members = await db.SetMembersAsync(indexKey);
        if (members.Length == 0)
        {
            return Array.Empty<RuntimeMetricsSnapshot>();
        }

        var memberValues = members
            .Where(static member => !member.IsNullOrEmpty)
            .Select(static member => member.ToString())
            .ToArray();

        if (memberValues.Length == 0)
        {
            return Array.Empty<RuntimeMetricsSnapshot>();
        }

        var keys = memberValues
            .Select(static member => (RedisKey)member)
            .ToArray();

        var values = await db.StringGetAsync(keys);
        var snapshots = new List<RuntimeMetricsSnapshot>(values.Length);

        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index];
            if (value.IsNullOrEmpty)
            {
                await db.SetRemoveAsync(indexKey, memberValues[index]);
                continue;
            }

            var snapshot = JsonSerializer.Deserialize<RuntimeMetricsSnapshot>(value!);
            if (snapshot is not null)
            {
                snapshots.Add(snapshot);
            }
        }

        return snapshots
            .OrderByDescending(static snapshot => snapshot.UpdatedAtUtc)
            .ToArray();
    }
}

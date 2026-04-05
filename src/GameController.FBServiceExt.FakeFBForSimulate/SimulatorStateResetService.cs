using System.Globalization;
using Microsoft.Data.SqlClient;
using StackExchange.Redis;

namespace GameController.FBServiceExt.FakeFBForSimulate;

internal sealed class SimulatorStateResetService
{
    private const string SqlResetCommandText = """
        SET NOCOUNT ON;
        DECLARE @accepted INT = (SELECT COUNT(*) FROM dbo.AcceptedVotes);
        DECLARE @normalized INT = (SELECT COUNT(*) FROM dbo.NormalizedEvents);
        DELETE FROM dbo.AcceptedVotes;
        DELETE FROM dbo.NormalizedEvents;
        SELECT @accepted AS AcceptedVotesDeleted, @normalized AS NormalizedEventsDeleted;
        """;

    private const string RedisCleanupScript = """
        local cursor = '0'
        local total = 0
        repeat
          local result = redis.call('SCAN', cursor, 'MATCH', ARGV[1], 'COUNT', 1000)
          cursor = result[1]
          local keys = result[2]
          local count = #keys
          if count > 0 then
            for startIndex = 1, count, 500 do
              local batch = {}
              local endIndex = math.min(startIndex + 499, count)
              for keyIndex = startIndex, endIndex do
                batch[#batch + 1] = keys[keyIndex]
              end
              redis.call('UNLINK', unpack(batch))
              total = total + #batch
            end
          end
        until cursor == '0'
        return total
        """;

    private readonly SimulatorDefaults _defaults;

    public SimulatorStateResetService(SimulatorDefaults defaults)
    {
        _defaults = defaults;
    }

    public async Task<SimulatorStateResetResult> ResetAsync(CancellationToken cancellationToken = default)
    {
        var sqlSummary = await ResetSqlAsync(cancellationToken).ConfigureAwait(false);
        var redisDeleted = await ResetRedisAsync().ConfigureAwait(false);

        return new SimulatorStateResetResult(
            sqlSummary.AcceptedVotesDeleted,
            sqlSummary.NormalizedEventsDeleted,
            redisDeleted);
    }

    private async Task<SqlResetSummary> ResetSqlAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_defaults.SqlConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = SqlResetCommandText;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new SqlResetSummary(0, 0);
        }

        return new SqlResetSummary(
            reader.GetInt32(0),
            reader.GetInt32(1));
    }

    private async Task<long> ResetRedisAsync()
    {
        var options = ConfigurationOptions.Parse(_defaults.RedisConnectionString);
        options.AbortOnConnectFail = false;

        await using var connection = await ConnectionMultiplexer.ConnectAsync(options).ConfigureAwait(false);
        var database = connection.GetDatabase();
        var deleted = await database.ScriptEvaluateAsync(
            RedisCleanupScript,
            Array.Empty<RedisKey>(),
            new RedisValue[] { $"{_defaults.RedisKeyPrefix}:*" }).ConfigureAwait(false);

        return deleted.Resp2Type == ResultType.Integer
            ? (long)deleted
            : long.Parse(deleted.ToString(), CultureInfo.InvariantCulture);
    }

    private readonly record struct SqlResetSummary(int AcceptedVotesDeleted, int NormalizedEventsDeleted);
}

internal readonly record struct SimulatorStateResetResult(
    int AcceptedVotesDeleted,
    int NormalizedEventsDeleted,
    long RedisKeysDeleted)
{
    public int TotalSqlRowsDeleted => AcceptedVotesDeleted + NormalizedEventsDeleted;
}


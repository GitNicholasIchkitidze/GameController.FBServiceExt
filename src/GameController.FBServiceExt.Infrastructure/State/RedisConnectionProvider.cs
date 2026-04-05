using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace GameController.FBServiceExt.Infrastructure.State;

public sealed class RedisConnectionProvider : IDisposable
{
    private readonly IOptionsMonitor<Options.RedisOptions> _optionsMonitor;
    private readonly ILogger<RedisConnectionProvider> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ConnectionMultiplexer? _connection;

    public RedisConnectionProvider(
        IOptionsMonitor<Options.RedisOptions> optionsMonitor,
        ILogger<RedisConnectionProvider> logger)
    {
        _optionsMonitor = optionsMonitor;
        _logger = logger;
    }

    public async ValueTask<ConnectionMultiplexer> GetConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsConnected: true })
        {
            return _connection;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_connection is { IsConnected: true })
            {
                return _connection;
            }

            _connection?.Dispose();
            _connection = null;

            var options = ConfigurationOptions.Parse(_optionsMonitor.CurrentValue.ConnectionString);
            options.AbortOnConnectFail = false;
            options.ClientName = $"{Environment.MachineName}:{AppDomain.CurrentDomain.FriendlyName}";

            _connection = await ConnectionMultiplexer.ConnectAsync(options);

            _logger.LogInformation("Redis connection established. Configuration: {Configuration}", _optionsMonitor.CurrentValue.ConnectionString);
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<IDatabase> GetDatabaseAsync(CancellationToken cancellationToken)
    {
        var connection = await GetConnectionAsync(cancellationToken);
        return connection.GetDatabase();
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _gate.Dispose();
    }
}


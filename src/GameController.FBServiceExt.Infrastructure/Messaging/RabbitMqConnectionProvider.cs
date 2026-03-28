using GameController.FBServiceExt.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace GameController.FBServiceExt.Infrastructure.Messaging;

internal sealed class RabbitMqConnectionProvider : IAsyncDisposable
{
    private readonly IOptionsMonitor<RabbitMqOptions> _optionsMonitor;
    private readonly ILogger<RabbitMqConnectionProvider> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;

    public RabbitMqConnectionProvider(
        IOptionsMonitor<RabbitMqOptions> optionsMonitor,
        ILogger<RabbitMqConnectionProvider> logger)
    {
        _optionsMonitor = optionsMonitor;
        _logger = logger;
    }

    public async ValueTask<IConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true })
        {
            return _connection;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_connection is { IsOpen: true })
            {
                return _connection;
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync();
                _connection = null;
            }

            var options = _optionsMonitor.CurrentValue;
            var factory = new ConnectionFactory
            {
                HostName = options.HostName,
                Port = options.Port,
                VirtualHost = options.VirtualHost,
                UserName = options.UserName,
                Password = options.Password,
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
                ConsumerDispatchConcurrency = 1,
                ClientProvidedName = $"{Environment.MachineName}:{AppDomain.CurrentDomain.FriendlyName}"
            };

            var connection = await factory.CreateConnectionAsync(cancellationToken);
            connection.ConnectionShutdownAsync += OnConnectionShutdownAsync;
            _connection = connection;

            _logger.LogInformation(
                "RabbitMQ connection established. Host: {Host}, Port: {Port}, VirtualHost: {VirtualHost}",
                options.HostName,
                options.Port,
                options.VirtualHost);

            return connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_connection is not null)
            {
                _connection.ConnectionShutdownAsync -= OnConnectionShutdownAsync;
                await _connection.DisposeAsync();
                _connection = null;
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private Task OnConnectionShutdownAsync(object sender, ShutdownEventArgs args)
    {
        _logger.LogWarning(
            "RabbitMQ connection shutdown. ReplyCode: {ReplyCode}, ReplyText: {ReplyText}, Initiator: {Initiator}",
            args.ReplyCode,
            args.ReplyText,
            args.Initiator);

        return Task.CompletedTask;
    }
}

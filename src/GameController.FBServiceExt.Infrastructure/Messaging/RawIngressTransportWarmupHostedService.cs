using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameController.FBServiceExt.Infrastructure.Messaging;

internal sealed class RawIngressTransportWarmupHostedService : IHostedService
{
    private readonly RabbitMqRawIngressPublisher _publisher;
    private readonly ILogger<RawIngressTransportWarmupHostedService> _logger;

    public RawIngressTransportWarmupHostedService(
        RabbitMqRawIngressPublisher publisher,
        ILogger<RawIngressTransportWarmupHostedService> logger)
    {
        _publisher = publisher;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {

        try
        {
            await _publisher.EnsureReadyAsync(cancellationToken);
            _logger.LogInformation("Raw ingress RabbitMQ transport warmed up.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to warm up raw ingress RabbitMQ transport during startup.");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
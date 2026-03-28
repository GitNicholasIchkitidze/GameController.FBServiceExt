using GameController.FBServiceExt.Application.Abstractions.Messaging;
using GameController.FBServiceExt.Application.Contracts.RawIngress;
using Microsoft.Extensions.Logging;

namespace GameController.FBServiceExt.Infrastructure.Messaging;

public sealed class NoOpRawIngressPublisher : IRawIngressPublisher
{
    private readonly ILogger<NoOpRawIngressPublisher> _logger;

    public NoOpRawIngressPublisher(ILogger<NoOpRawIngressPublisher> logger)
    {
        _logger = logger;
    }

    public ValueTask PublishAsync(RawWebhookEnvelope envelope, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Raw ingress envelope accepted. EnvelopeId: {EnvelopeId}, Source: {Source}, RequestId: {RequestId}",
            envelope.EnvelopeId,
            envelope.Source,
            envelope.RequestId);

        return ValueTask.CompletedTask;
    }
}

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

    public ValueTask PublishAsync(RawIngressPublishRequest publishRequest, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Raw ingress body accepted. EnvelopeId: {EnvelopeId}, Source: {Source}, RequestId: {RequestId}, BodyBytes: {BodyBytes}",
            publishRequest.EnvelopeId,
            publishRequest.Source,
            publishRequest.RequestId,
            publishRequest.BodyUtf8.Length);

        return ValueTask.CompletedTask;
    }
}

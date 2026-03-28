using GameController.FBServiceExt.Application.Abstractions.Messaging;
using GameController.FBServiceExt.Application.Contracts.Normalization;
using Microsoft.Extensions.Logging;

namespace GameController.FBServiceExt.Infrastructure.Messaging;

public sealed class NoOpNormalizedEventPublisher : INormalizedEventPublisher
{
    private readonly ILogger<NoOpNormalizedEventPublisher> _logger;

    public NoOpNormalizedEventPublisher(ILogger<NoOpNormalizedEventPublisher> logger)
    {
        _logger = logger;
    }

    public ValueTask PublishBatchAsync(IReadOnlyCollection<NormalizedMessengerEvent> events, CancellationToken cancellationToken)
    {
        if (events.Count > 0)
        {
            _logger.LogInformation("Published normalized event batch. Count: {Count}", events.Count);
        }

        return ValueTask.CompletedTask;
    }
}

using GameController.FBServiceExt.Application.Contracts.Observability;

namespace GameController.FBServiceExt.Application.Abstractions.Observability;

public interface IRabbitMqQueueMetricsReader
{
    ValueTask<IReadOnlyList<RabbitMqQueueMetricsSnapshot>> GetQueuesAsync(CancellationToken cancellationToken);
}

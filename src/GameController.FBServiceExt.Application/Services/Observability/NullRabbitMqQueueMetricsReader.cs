using GameController.FBServiceExt.Application.Abstractions.Observability;
using GameController.FBServiceExt.Application.Contracts.Observability;

namespace GameController.FBServiceExt.Application.Services.Observability;

public sealed class NullRabbitMqQueueMetricsReader : IRabbitMqQueueMetricsReader
{
    public ValueTask<IReadOnlyList<RabbitMqQueueMetricsSnapshot>> GetQueuesAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult<IReadOnlyList<RabbitMqQueueMetricsSnapshot>>(Array.Empty<RabbitMqQueueMetricsSnapshot>());
}

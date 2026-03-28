namespace GameController.FBServiceExt.Application.Abstractions.State;

public interface IEventDeduplicationStore
{
    ValueTask<bool> IsProcessedAsync(string eventId, CancellationToken cancellationToken);

    ValueTask MarkProcessedAsync(string eventId, TimeSpan retention, CancellationToken cancellationToken);
}

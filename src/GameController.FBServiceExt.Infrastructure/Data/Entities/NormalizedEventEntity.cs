using GameController.FBServiceExt.Domain.Messaging;

namespace GameController.FBServiceExt.Infrastructure.Data.Entities;

internal sealed class NormalizedEventEntity
{
    public long Id { get; set; }

    public string EventId { get; set; } = string.Empty;

    public Guid RawEnvelopeId { get; set; }

    public MessengerEventType EventType { get; set; }

    public string? MessageId { get; set; }

    public string? SenderId { get; set; }

    public string? RecipientId { get; set; }

    public DateTime OccurredAtUtc { get; set; }

    public string PayloadJson { get; set; } = string.Empty;

    public DateTime RecordedAtUtc { get; set; }
}

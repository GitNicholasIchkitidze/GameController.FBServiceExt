using GameController.FBServiceExt.Domain.Messaging;

namespace GameController.FBServiceExt.Application.Contracts.Normalization;

public sealed record NormalizedMessengerEvent(
    string EventId,
    MessengerEventType EventType,
    string? MessageId,
    string? SenderId,
    string? RecipientId,
    DateTime OccurredAtUtc,
    string PayloadJson,
    Guid RawEnvelopeId);

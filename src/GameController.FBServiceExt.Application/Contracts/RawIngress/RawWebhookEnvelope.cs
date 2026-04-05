namespace GameController.FBServiceExt.Application.Contracts.RawIngress;

public sealed record RawWebhookEnvelope(
    Guid EnvelopeId,
    string Source,
    string RequestId,
    DateTime ReceivedAtUtc,
    string Body);

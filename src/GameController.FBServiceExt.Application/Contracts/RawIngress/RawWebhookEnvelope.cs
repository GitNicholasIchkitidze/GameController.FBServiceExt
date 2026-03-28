namespace GameController.FBServiceExt.Application.Contracts.RawIngress;

public sealed record RawWebhookEnvelope(
    Guid EnvelopeId,
    string Source,
    string RequestId,
    DateTime ReceivedAtUtc,
    IReadOnlyDictionary<string, string[]> Headers,
    string Body);

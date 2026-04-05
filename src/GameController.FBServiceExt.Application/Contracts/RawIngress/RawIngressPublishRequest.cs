namespace GameController.FBServiceExt.Application.Contracts.RawIngress;

public sealed record RawIngressPublishRequest(
    Guid EnvelopeId,
    string Source,
    string RequestId,
    DateTime ReceivedAtUtc,
    byte[] BodyUtf8);

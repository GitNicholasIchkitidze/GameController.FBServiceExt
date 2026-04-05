namespace GameController.FBServiceExt.Application.Contracts.Ingress;

public sealed record AcceptWebhookCommand(
    string RequestId,
    byte[] BodyUtf8,
    DateTime? ReceivedAtUtc = null);

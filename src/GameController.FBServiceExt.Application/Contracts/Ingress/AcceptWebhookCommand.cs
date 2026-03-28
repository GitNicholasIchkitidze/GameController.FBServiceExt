namespace GameController.FBServiceExt.Application.Contracts.Ingress;

public sealed record AcceptWebhookCommand(
    string RequestId,
    IReadOnlyDictionary<string, string[]> Headers,
    string Body,
    DateTime? ReceivedAtUtc = null);

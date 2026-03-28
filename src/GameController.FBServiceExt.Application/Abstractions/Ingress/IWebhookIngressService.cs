using GameController.FBServiceExt.Application.Contracts.Ingress;

namespace GameController.FBServiceExt.Application.Abstractions.Ingress;

public interface IWebhookIngressService
{
    ValueTask AcceptAsync(AcceptWebhookCommand command, CancellationToken cancellationToken);
}

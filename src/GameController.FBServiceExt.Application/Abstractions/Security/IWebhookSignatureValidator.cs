namespace GameController.FBServiceExt.Application.Abstractions.Security;

public interface IWebhookSignatureValidator
{
    bool IsValid(string requestBody, string? signatureHeader);
}

namespace GameController.FBServiceExt.Application.Abstractions.Security;

public interface IWebhookSignatureValidator
{
    bool IsValid(ReadOnlyMemory<byte> requestBodyUtf8, string? signatureHeader);
}

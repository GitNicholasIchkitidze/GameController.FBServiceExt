namespace GameController.FBServiceExt.Application.Abstractions.Messaging;

public interface IOutboundMessengerClient
{
    ValueTask<bool> SendTextAsync(string recipientId, string messageText, CancellationToken cancellationToken);

    ValueTask<bool> SendButtonTemplateAsync(
        string recipientId,
        string promptText,
        IReadOnlyCollection<MessengerPostbackButton> buttons,
        CancellationToken cancellationToken);

    ValueTask<bool> SendGenericTemplateAsync(
        string recipientId,
        IReadOnlyCollection<MessengerGenericTemplateElement> elements,
        CancellationToken cancellationToken);
}

public sealed record MessengerPostbackButton(string Title, string Payload);

public sealed record MessengerGenericTemplateElement(
    string Title,
    string? Subtitle,
    string? ImageUrl,
    IReadOnlyCollection<MessengerPostbackButton> Buttons);

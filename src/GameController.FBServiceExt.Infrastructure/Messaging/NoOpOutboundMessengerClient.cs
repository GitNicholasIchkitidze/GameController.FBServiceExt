using GameController.FBServiceExt.Application.Abstractions.Messaging;
using Microsoft.Extensions.Logging;

namespace GameController.FBServiceExt.Infrastructure.Messaging;

internal sealed class NoOpOutboundMessengerClient : IOutboundMessengerClient
{
    private readonly ILogger<NoOpOutboundMessengerClient> _logger;

    public NoOpOutboundMessengerClient(ILogger<NoOpOutboundMessengerClient> logger)
    {
        _logger = logger;
    }

    public ValueTask<bool> SendTextAsync(string recipientId, string messageText, CancellationToken cancellationToken)
    {
        _logger.LogDebug("No-op Messenger text send. RecipientId: {RecipientId}", recipientId);
        return ValueTask.FromResult(true);
    }

    public ValueTask<bool> SendButtonTemplateAsync(
        string recipientId,
        string promptText,
        IReadOnlyCollection<MessengerPostbackButton> buttons,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "No-op Messenger button template send. RecipientId: {RecipientId}, ButtonCount: {ButtonCount}",
            recipientId,
            buttons.Count);
        return ValueTask.FromResult(true);
    }

    public ValueTask<bool> SendGenericTemplateAsync(
        string recipientId,
        IReadOnlyCollection<MessengerGenericTemplateElement> elements,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "No-op Messenger generic template send. RecipientId: {RecipientId}, ElementCount: {ElementCount}",
            recipientId,
            elements.Count);
        return ValueTask.FromResult(true);
    }
}

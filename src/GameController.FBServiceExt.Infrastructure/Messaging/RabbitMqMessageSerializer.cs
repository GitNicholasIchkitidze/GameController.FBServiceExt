using System.Text.Json;
using GameController.FBServiceExt.Application.Contracts.Normalization;
using GameController.FBServiceExt.Application.Contracts.RawIngress;

namespace GameController.FBServiceExt.Infrastructure.Messaging;

internal static class RabbitMqMessageSerializer
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.General);

    public static ReadOnlyMemory<byte> Serialize(RawWebhookEnvelope envelope)
    {
        return JsonSerializer.SerializeToUtf8Bytes(envelope, SerializerOptions);
    }

    public static ReadOnlyMemory<byte> Serialize(NormalizedMessengerEvent normalizedEvent)
    {
        return JsonSerializer.SerializeToUtf8Bytes(normalizedEvent, SerializerOptions);
    }

    public static RawWebhookEnvelope DeserializeRawEnvelope(ReadOnlyMemory<byte> body)
    {
        return JsonSerializer.Deserialize<RawWebhookEnvelope>(body.Span, SerializerOptions)
            ?? throw new InvalidOperationException("RabbitMQ raw ingress payload could not be deserialized.");
    }

    public static NormalizedMessengerEvent DeserializeNormalizedEvent(ReadOnlyMemory<byte> body)
    {
        return JsonSerializer.Deserialize<NormalizedMessengerEvent>(body.Span, SerializerOptions)
            ?? throw new InvalidOperationException("RabbitMQ normalized event payload could not be deserialized.");
    }
}

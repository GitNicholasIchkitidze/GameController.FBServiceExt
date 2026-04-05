using System.Text.Json;
using StackExchange.Redis;

namespace GameController.FBServiceExt.FakeFBForSimulate;

internal sealed class RedisFakeMetaOutboundSubscription : IAsyncDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly SimulatorDefaults _defaults;
    private readonly Action<FakeOutboundMessage> _capture;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ConnectionMultiplexer? _connection;
    private ISubscriber? _subscriber;
    private RedisChannel _channel;
    private bool _started;

    public RedisFakeMetaOutboundSubscription(
        SimulatorDefaults defaults,
        Action<FakeOutboundMessage> capture,
        Action<string> log)
    {
        _defaults = defaults;
        _capture = capture;
        _log = log;
        _channel = GetChannel(defaults.RedisKeyPrefix);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started)
            {
                return;
            }

            var options = ConfigurationOptions.Parse(_defaults.RedisConnectionString);
            options.AbortOnConnectFail = false;
            options.ClientName = $"{Environment.MachineName}:FakeFBForSimulate";

            _connection = await ConnectionMultiplexer.ConnectAsync(options).ConfigureAwait(false);
            _subscriber = _connection.GetSubscriber();
            await _subscriber.SubscribeAsync(_channel, HandlePublishedMessage).ConfigureAwait(false);
            _started = true;
            _log($"Headless fake-meta subscription ready on redis channel '{_channel}'.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private void HandlePublishedMessage(RedisChannel channel, RedisValue value)
    {
        try
        {
            if (value.IsNullOrEmpty)
            {
                return;
            }

            var published = JsonSerializer.Deserialize<PublishedFakeMetaOutboundMessage>(value!, SerializerOptions);
            if (published is null)
            {
                return;
            }

            var outbound = new FakeOutboundMessage(
                published.Sequence,
                published.RecipientId,
                published.Version,
                published.Kind,
                published.Text,
                published.TemplateType,
                published.Elements.Select(static element => new FakeTemplateElement(
                    element.Title,
                    element.Subtitle,
                    element.ImageUrl,
                    element.Buttons.Select(static button => new FakeButton(button.Title, button.Payload, button.Type)).ToArray())).ToArray(),
                published.Buttons.Select(static button => new FakeButton(button.Title, button.Payload, button.Type)).ToArray());

            _capture(outbound);
        }
        catch (Exception exception)
        {
            _log($"Headless fake-meta subscription parse failure: {exception.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_subscriber is not null)
            {
                await _subscriber.UnsubscribeAsync(_channel).ConfigureAwait(false);
            }

            if (_connection is not null)
            {
                await _connection.CloseAsync().ConfigureAwait(false);
                _connection.Dispose();
            }

            _subscriber = null;
            _connection = null;
            _started = false;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private static RedisChannel GetChannel(string prefix) => new($"{prefix}:fake-meta:channel", RedisChannel.PatternMode.Literal);

    private sealed record PublishedFakeMetaOutboundMessage(
        long Sequence,
        string RecipientId,
        string Version,
        DateTime CapturedAtUtc,
        string Kind,
        string? Text,
        string? TemplateType,
        IReadOnlyList<PublishedFakeMetaTemplateElement> Elements,
        IReadOnlyList<PublishedFakeMetaButton> Buttons);

    private sealed record PublishedFakeMetaTemplateElement(
        string? Title,
        string? Subtitle,
        string? ImageUrl,
        IReadOnlyList<PublishedFakeMetaButton> Buttons);

    private sealed record PublishedFakeMetaButton(
        string? Title,
        string? Payload,
        string? Type);
}

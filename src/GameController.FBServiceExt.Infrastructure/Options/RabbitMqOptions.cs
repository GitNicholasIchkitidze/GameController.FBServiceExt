namespace GameController.FBServiceExt.Infrastructure.Options;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public string HostName { get; set; } = "localhost";

    public int Port { get; set; } = 5672;

    public string VirtualHost { get; set; } = "/";

    public string UserName { get; set; } = "guest";

    public string Password { get; set; } = "guest";

    public string RawIngressQueueName { get; set; } = "fbserviceext.raw.ingress";

    public string NormalizedEventQueueName { get; set; } = "fbserviceext.normalized.events";

    public ushort PrefetchCount { get; set; } = 64;

    public int PublisherChannelPoolSize { get; set; } = 16;

    public bool PublisherConfirmationsEnabled { get; set; } = true;

    public bool PublisherConfirmationTrackingEnabled { get; set; } = true;

    public string ManagementApiBaseUrl { get; set; } = "http://127.0.0.1:15672/api";
}


using GameController.FBServiceExt.Application.Abstractions.Ingress;
using GameController.FBServiceExt.Application.Abstractions.Processing;
using GameController.FBServiceExt.Application.Abstractions.State;
using GameController.FBServiceExt.Application.Abstractions.Observability;
using GameController.FBServiceExt.Application.Options;
using GameController.FBServiceExt.Application.Services.Observability;
using GameController.FBServiceExt.Application.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GameController.FBServiceExt.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddIngressApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IRuntimeMetricsCollector, NullRuntimeMetricsCollector>();
        services.TryAddSingleton<IRuntimeMetricsSnapshotReader, NullRuntimeMetricsSnapshotReader>();
        services.TryAddSingleton<IRabbitMqQueueMetricsReader, NullRabbitMqQueueMetricsReader>();

        services.AddOptions<WebhookIngressOptions>()
            .Bind(configuration.GetSection(WebhookIngressOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.Source), "Webhook ingress source is required.")
            .Validate(options => options.MaxRequestBodySizeBytes > 0, "Webhook ingress max request body size must be greater than zero.")
            .ValidateOnStart();

        services.AddOptions<MetaWebhookOptions>()
            .Bind(configuration.GetSection(MetaWebhookOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.VerifyToken), "Meta webhook verify token is required.")
            .Validate(options => !options.RequireSignatureValidation || !string.IsNullOrWhiteSpace(options.AppSecret), "Meta webhook app secret is required when signature validation is enabled.")
            .ValidateOnStart();

        services.AddSingleton<IWebhookIngressService, WebhookIngressService>();

        return services;
    }

    public static IServiceCollection AddProcessingApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IRuntimeMetricsCollector, NullRuntimeMetricsCollector>();
        services.TryAddSingleton<IRuntimeMetricsSnapshotReader, NullRuntimeMetricsSnapshotReader>();
        services.TryAddSingleton<IRabbitMqQueueMetricsReader, NullRabbitMqQueueMetricsReader>();

        services.AddOptions<VotingWorkflowOptions>()
            .Bind(configuration.GetSection(VotingWorkflowOptions.SectionName))
            .Validate(options => options.OptionsSessionTtl > TimeSpan.Zero, "Voting options session TTL must be greater than zero.")
            .Validate(options => options.ConfirmationTimeout > TimeSpan.Zero, "Voting confirmation timeout must be greater than zero.")
            .Validate(options => options.Cooldown > TimeSpan.Zero, "Voting cooldown must be greater than zero.")
            .Validate(options => options.ProcessedEventRetention > TimeSpan.Zero, "Processed event retention must be greater than zero.")
            .Validate(options => options.ProcessingLockTimeout > TimeSpan.Zero, "Processing lock timeout must be greater than zero.")
            .ValidateOnStart();

        services.AddOptions<CandidatesOptions>()
            .Bind(configuration.GetSection(CandidatesOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IRawWebhookNormalizer, RawWebhookNormalizer>();
        services.AddSingleton<INormalizedEventProcessor, NormalizedEventProcessor>();

        return services;
    }
}


using GameController.FBServiceExt.Application.Abstractions.Messaging;
using GameController.FBServiceExt.Application.Abstractions.Observability;
using GameController.FBServiceExt.Application.Abstractions.Persistence;
using GameController.FBServiceExt.Application.Abstractions.Security;
using GameController.FBServiceExt.Application.Abstractions.State;
using GameController.FBServiceExt.Infrastructure.Data;
using GameController.FBServiceExt.Infrastructure.HealthChecks;
using GameController.FBServiceExt.Infrastructure.Messaging;
using GameController.FBServiceExt.Infrastructure.Observability;
using GameController.FBServiceExt.Infrastructure.Options;
using GameController.FBServiceExt.Infrastructure.Persistence;
using GameController.FBServiceExt.Infrastructure.Security;
using GameController.FBServiceExt.Infrastructure.State;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GameController.FBServiceExt.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddIngressInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RabbitMqOptions>()
            .Bind(configuration.GetSection(RabbitMqOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.HostName), "RabbitMQ host name is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.RawIngressQueueName), "RabbitMQ raw ingress queue name is required.")
            .ValidateOnStart();

        services.AddSingleton<RabbitMqConnectionProvider>();
        services.AddSingleton<IWebhookSignatureValidator, MetaWebhookSignatureValidator>();
        services.AddSingleton<RabbitMqRawIngressPublisher>();
        services.AddSingleton<IRawIngressPublisher>(serviceProvider => serviceProvider.GetRequiredService<RabbitMqRawIngressPublisher>());
        services.AddHostedService<RawIngressTransportWarmupHostedService>();

        services.AddHealthChecks()
            .AddCheck<RabbitMqConfigurationHealthCheck>("rabbitmq");

        AddRuntimeMetricsInfrastructure(services, configuration, includeRabbitMqQueueReader: true);

        return services;
    }

    public static IServiceCollection AddProcessingInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RabbitMqOptions>()
            .Bind(configuration.GetSection(RabbitMqOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.HostName), "RabbitMQ host name is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.RawIngressQueueName), "RabbitMQ raw ingress queue name is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.NormalizedEventQueueName), "RabbitMQ normalized event queue name is required.")
            .ValidateOnStart();

        services.AddOptions<RedisOptions>()
            .Bind(configuration.GetSection(RedisOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.ConnectionString), "Redis connection string is required.")
            .ValidateOnStart();

        services.AddOptions<SqlStorageOptions>()
            .Bind(configuration.GetSection(SqlStorageOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.ConnectionString), "SQL storage connection string is required.")
            .ValidateOnStart();

        services.AddDbContextFactory<FbServiceExtDbContext>((serviceProvider, optionsBuilder) =>
        {
            var sqlStorageOptions = serviceProvider.GetRequiredService<IOptionsMonitor<SqlStorageOptions>>().CurrentValue;
            optionsBuilder.UseSqlServer(
                sqlStorageOptions.ConnectionString,
                sqlOptions => sqlOptions.EnableRetryOnFailure(
                    maxRetryCount: 5,
                    maxRetryDelay: TimeSpan.FromSeconds(10),
                    errorNumbersToAdd: null));
        });

        services.AddSingleton<RabbitMqConnectionProvider>();
        services.AddSingleton<RedisConnectionProvider>();
        services.AddSingleton<IRawIngressConsumer, RabbitMqRawIngressConsumer>();
        services.AddSingleton<INormalizedEventPublisher, RabbitMqNormalizedEventPublisher>();
        services.AddSingleton<INormalizedEventConsumer, RabbitMqNormalizedEventConsumer>();
        services.AddSingleton<IEventDeduplicationStore, RedisEventDeduplicationStore>();
        services.AddSingleton<IUserProcessingLockManager, RedisUserProcessingLockManager>();
        services.AddSingleton<IVoteSessionStore, RedisVoteSessionStore>();
        services.AddSingleton<INormalizedEventStore, SqlNormalizedEventStore>();
        services.AddSingleton<IAcceptedVoteStore, SqlAcceptedVoteStore>();
        services.AddHostedService<SqlSchemaInitializerHostedService>();

        services.AddHealthChecks()
            .AddCheck<RabbitMqConfigurationHealthCheck>("rabbitmq")
            .AddCheck<RedisConfigurationHealthCheck>("redis")
            .AddCheck<SqlStorageConfigurationHealthCheck>("sql");

        AddRuntimeMetricsInfrastructure(services, configuration, includeRabbitMqQueueReader: false);

        return services;
    }

    private static void AddRuntimeMetricsInfrastructure(IServiceCollection services, IConfiguration configuration, bool includeRabbitMqQueueReader)
    {
        var metricsSection = configuration.GetSection(RuntimeMetricsOptions.SectionName);
        services.AddOptions<RuntimeMetricsOptions>()
            .Bind(metricsSection);

        var metricsOptions = metricsSection.Get<RuntimeMetricsOptions>() ?? new RuntimeMetricsOptions();
        if (!metricsOptions.Enabled)
        {
            return;
        }

        services.AddOptions<RedisOptions>()
            .Bind(configuration.GetSection(RedisOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.ConnectionString), "Redis connection string is required for runtime metrics.")
            .ValidateOnStart();

        services.AddSingleton<RedisConnectionProvider>();
        services.AddSingleton<IRuntimeMetricsCollector, InMemoryRuntimeMetricsCollector>();
        services.AddSingleton<IRuntimeMetricsSnapshotReader, RedisRuntimeMetricsSnapshotReader>();
        services.AddHostedService<RedisRuntimeMetricsPublisherHostedService>();

        if (includeRabbitMqQueueReader)
        {
            services.AddHttpClient<IRabbitMqQueueMetricsReader, RabbitMqQueueMetricsReader>(client => client.Timeout = TimeSpan.FromSeconds(3));
        }
    }
}

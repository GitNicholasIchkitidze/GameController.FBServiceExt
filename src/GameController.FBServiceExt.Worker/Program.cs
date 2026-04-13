using System.Diagnostics;
using System.Reflection;
using GameController.FBServiceExt.Application;
using GameController.FBServiceExt.Application.Options;
using GameController.FBServiceExt.Infrastructure;
using InfrastructureDependencyInjection = GameController.FBServiceExt.Infrastructure.DependencyInjection;
using GameController.FBServiceExt.Infrastructure.Logging;
using GameController.FBServiceExt.Worker.Options;
using GameController.FBServiceExt.Worker.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    var environmentName = builder.Environment.EnvironmentName;
    builder.Configuration.Sources.Clear();
    var sharedConfigPaths = AddSharedJsonFiles(builder.Configuration, builder.Environment.ContentRootPath, environmentName);
    builder.Configuration
        .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
        .AddJsonFile($"appsettings.{environmentName}.json", optional: true, reloadOnChange: true);

    builder.Configuration.AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true);

    builder.Configuration.AddEnvironmentVariables();

    if (args is { Length: > 0 })
    {
        builder.Configuration.AddCommandLine(args);
    }

    Log.Information(
        "Worker configuration initialized. Environment={EnvironmentName}, ContentRoot={ContentRootPath}, SharedConfigs={SharedConfigs}, VotingSecretConfigured={VotingSecretConfigured}, DataErasureSecretConfigured={DataErasureSecretConfigured}",
        environmentName,
        builder.Environment.ContentRootPath,
        sharedConfigPaths,
        HasConfiguredSecret(builder.Configuration, "VotingWorkflow:PayloadSignatureSecret"),
        HasConfiguredSecret(builder.Configuration, "DataErasure:ConfirmationPayloadSecret"));
    var metaMessengerOptions = builder.Configuration.GetSection(MetaMessengerOptions.SectionName).Get<MetaMessengerOptions>() ?? new MetaMessengerOptions();
    var outboundMode = InfrastructureDependencyInjection.ResolveOutboundMessengerClientMode(metaMessengerOptions);
    Log.Information(
        "Worker outbound messenger configured. Mode={OutboundMode}, Enabled={Enabled}, TokenConfigured={TokenConfigured}, GraphApiBaseUrl={GraphApiBaseUrl}, SimulatorGraphApiBaseUrl={SimulatorGraphApiBaseUrl}",
        outboundMode,
        metaMessengerOptions.Enabled,
        HasConfiguredSecret(builder.Configuration, "MetaMessenger:PageAccessToken"),
        ResolveEffectiveGraphApiBaseUrl(metaMessengerOptions.GraphApiBaseUrl),
        ResolveEffectiveSimulatorGraphApiBaseUrl(metaMessengerOptions.SimulatorGraphApiBaseUrl));

    builder.Logging.Configure(options =>
    {
        options.ActivityTrackingOptions =
            ActivityTrackingOptions.TraceId |
            ActivityTrackingOptions.SpanId |
            ActivityTrackingOptions.ParentId |
            ActivityTrackingOptions.Tags |
            ActivityTrackingOptions.Baggage;
    });

    builder.Logging.ClearProviders();
    builder.Services.AddSerilog((services, loggerConfiguration) =>
    {
        loggerConfiguration
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext();

        if (builder.Environment.IsDevelopment())
        {
            loggerConfiguration.Enrich.With(new CallerInfoEnricher("GameController.FBServiceExt"));
        }
    });

    builder.Services.AddOptions<WorkerExecutionOptions>()
        .Bind(builder.Configuration.GetSection(WorkerExecutionOptions.SectionName))
        .Validate(options => options.RawIngressParallelism > 0, "Raw ingress parallelism must be greater than zero.")
        .Validate(options => options.NormalizedProcessingParallelism > 0, "Normalized processing parallelism must be greater than zero.")
        .ValidateOnStart();

    builder.Services.AddProcessingApplication(builder.Configuration);
    builder.Services.AddProcessingInfrastructure(builder.Configuration);
    builder.Services.AddHostedService<RawIngressNormalizerWorker>();
    builder.Services.AddHostedService<NormalizedEventProcessorWorker>();

    var host = builder.Build();
    await host.RunAsync();
}
catch (Exception ex)
{
    Environment.ExitCode = 1;
    Console.Error.WriteLine(ex);
    Log.Fatal(ex, "GameController.FBServiceExt.Worker terminated unexpectedly.");
}
finally
{
    Log.CloseAndFlush();
}

static string[] AddSharedJsonFiles(IConfigurationBuilder configurationBuilder, string contentRootPath, string environmentName)
{
    var sharedPaths = ResolveSharedConfigPaths(contentRootPath, environmentName).ToArray();
    foreach (var sharedPath in sharedPaths)
    {
        configurationBuilder.AddJsonFile(sharedPath, optional: false, reloadOnChange: true);
    }

    return sharedPaths;
}

static IEnumerable<string> ResolveSharedConfigPaths(string contentRootPath, string environmentName)
{
    foreach (var root in EnumerateCandidateRoots(contentRootPath))
    {
        var shared = Path.Combine(root.FullName, "appsettings.Shared.json");
        if (!File.Exists(shared))
        {
            continue;
        }

        yield return shared;
        var environmentShared = Path.Combine(root.FullName, $"appsettings.Shared.{environmentName}.json");
        if (File.Exists(environmentShared))
        {
            yield return environmentShared;
        }

        yield break;
    }
}

static IEnumerable<DirectoryInfo> EnumerateCandidateRoots(string contentRootPath)
{
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var startPath in new[]
             {
                 contentRootPath,
                 AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
             })
    {
        if (string.IsNullOrWhiteSpace(startPath) || !Directory.Exists(startPath))
        {
            continue;
        }

        var current = new DirectoryInfo(startPath);
        while (current is not null && seen.Add(current.FullName))
        {
            yield return current;
            current = current.Parent;
        }
    }
}

static bool HasConfiguredSecret(IConfiguration configuration, string key)
    => !string.IsNullOrWhiteSpace(configuration[key]);
static string ResolveEffectiveGraphApiBaseUrl(string? baseUrl)
    => string.IsNullOrWhiteSpace(baseUrl)
        ? "https://graph.facebook.com"
        : baseUrl.Trim().TrimEnd('/');
static string ResolveEffectiveSimulatorGraphApiBaseUrl(string? baseUrl)
    => string.IsNullOrWhiteSpace(baseUrl)
        ? MetaMessengerOptions.DefaultSimulatorGraphApiBaseUrl
        : baseUrl.Trim().TrimEnd('/');




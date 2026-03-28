using System.Diagnostics;
using GameController.FBServiceExt.Application;
using GameController.FBServiceExt.Infrastructure;
using GameController.FBServiceExt.Infrastructure.Logging;
using GameController.FBServiceExt.Worker.Options;
using GameController.FBServiceExt.Worker.Services;
using Microsoft.Extensions.Logging;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

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
    Log.Fatal(ex, "GameController.FBServiceExt.Worker terminated unexpectedly.");
}
finally
{
    Log.CloseAndFlush();
}

using System.Diagnostics;
using System.Net;
using GameController.FBServiceExt.Application;
using GameController.FBServiceExt.DevLogs;
using GameController.FBServiceExt.DevMetrics;
using GameController.FBServiceExt.Infrastructure;
using GameController.FBServiceExt.Infrastructure.Logging;
using GameController.FBServiceExt.Options;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Logging.Configure(options =>
    {
        options.ActivityTrackingOptions =
            ActivityTrackingOptions.TraceId |
            ActivityTrackingOptions.SpanId |
            ActivityTrackingOptions.ParentId |
            ActivityTrackingOptions.Tags |
            ActivityTrackingOptions.Baggage;
    });

    builder.Host.UseSerilog((context, services, loggerConfiguration) =>
    {
        loggerConfiguration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext();

        if (builder.Environment.IsDevelopment())
        {
            loggerConfiguration.Enrich.With(new CallerInfoEnricher("GameController.FBServiceExt"));
        }
    });

    builder.Services.AddProblemDetails();
    builder.Services.AddControllers();
    builder.Services.AddSingleton<DevMetricsDashboardService>();
    builder.Services.AddOptions<DevLogViewerOptions>()
        .Bind(builder.Configuration.GetSection(DevLogViewerOptions.SectionName));
    builder.Services.AddHttpClient<GraylogLogViewerService>(client => client.Timeout = TimeSpan.FromSeconds(5));
    builder.Services.AddIngressApplication(builder.Configuration);
    builder.Services.AddIngressInfrastructure(builder.Configuration);

    var app = builder.Build();

    app.UseSerilogRequestLogging(options =>
    {
        options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} => {StatusCode} in {Elapsed:0.0000} ms";
        options.GetLevel = (httpContext, elapsed, ex) =>
        {
            var path = httpContext.Request.Path;

            if (ex is not null || httpContext.Response.StatusCode >= StatusCodes.Status500InternalServerError)
            {
                return LogEventLevel.Error;
            }

            if (path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWithSegments("/dev/logs", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWithSegments("/dev-logs", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWithSegments("/dev/metrics", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWithSegments("/dev-metrics", StringComparison.OrdinalIgnoreCase))
            {
                return LogEventLevel.Verbose;
            }

            if (path.StartsWithSegments("/api/facebook/webhooks", StringComparison.OrdinalIgnoreCase) &&
                httpContext.Response.StatusCode < StatusCodes.Status400BadRequest &&
                elapsed < 1_000)
            {
                return LogEventLevel.Debug;
            }

            if (httpContext.Response.StatusCode >= StatusCodes.Status400BadRequest || elapsed >= 2_000)
            {
                return LogEventLevel.Warning;
            }

            return LogEventLevel.Information;
        };

        options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
        {
            diagnosticContext.Set("RequestMethod", httpContext.Request.Method);
            diagnosticContext.Set("RequestPath", httpContext.Request.Path.Value ?? string.Empty);
            diagnosticContext.Set("TraceIdentifier", httpContext.TraceIdentifier);
            diagnosticContext.Set("RemoteIpAddress", httpContext.Connection.RemoteIpAddress?.ToString());
            diagnosticContext.Set("UserAgent", httpContext.Request.Headers.UserAgent.ToString());
        };
    });

    app.UseExceptionHandler();
    app.Use(async (context, next) =>
    {
        if ((context.Request.Path.StartsWithSegments("/dev/logs", StringComparison.OrdinalIgnoreCase) ||
             context.Request.Path.StartsWithSegments("/dev-logs", StringComparison.OrdinalIgnoreCase) ||
             context.Request.Path.StartsWithSegments("/dev/metrics", StringComparison.OrdinalIgnoreCase) ||
             context.Request.Path.StartsWithSegments("/dev-metrics", StringComparison.OrdinalIgnoreCase)) &&
            !IsLocalRequest(context))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await next();
    });
    app.UseStaticFiles();

    var devLogViewerOptions = app.Services.GetRequiredService<IOptionsMonitor<DevLogViewerOptions>>();
    var devToolsEnabled = app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Performance") || devLogViewerOptions.CurrentValue.Enabled;
    if (devToolsEnabled)
    {
        app.MapGet("/dev/logs", () => Results.Redirect("/dev-logs/index.html"));
        app.MapGet("/dev/logs/api", async (string? query, int? limit, GraylogLogViewerService service, CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await service.SearchAsync(query, limit, cancellationToken);
                return Results.Ok(result);
            }
            catch (HttpRequestException exception)
            {
                return Results.Problem(
                    detail: exception.Message,
                    statusCode: StatusCodes.Status502BadGateway,
                    title: "Graylog query failed.");
            }
        });

        app.MapGet("/dev/metrics", () => Results.Redirect("/dev-metrics/index.html"));
        app.MapGet("/dev/metrics/api", async (DevMetricsDashboardService service, CancellationToken cancellationToken) =>
        {
            try
            {
                var snapshot = await service.GetSnapshotAsync(cancellationToken);
                return Results.Ok(snapshot);
            }
            catch (HttpRequestException exception)
            {
                return Results.Problem(
                    detail: exception.Message,
                    statusCode: StatusCodes.Status502BadGateway,
                    title: "Metrics query failed.");
            }
        });
    }

    app.MapHealthChecks("/health/ready", new HealthCheckOptions());
    app.MapGet("/health/live", () => Results.Ok(new
    {
        status = "ok",
        service = "GameController.FBServiceExt",
        utc = DateTime.UtcNow
    }));

    app.MapControllers();

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "GameController.FBServiceExt host terminated unexpectedly.");
}
finally
{
    Log.CloseAndFlush();
}

static bool IsLocalRequest(HttpContext httpContext)
{
    var remoteIpAddress = httpContext.Connection.RemoteIpAddress;
    if (remoteIpAddress is null)
    {
        return false;
    }

    if (IPAddress.IsLoopback(remoteIpAddress))
    {
        return true;
    }

    var localIpAddress = httpContext.Connection.LocalIpAddress;
    return localIpAddress is not null && remoteIpAddress.Equals(localIpAddress);
}

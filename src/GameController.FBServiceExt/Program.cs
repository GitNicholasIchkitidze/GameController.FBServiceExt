using System.Reflection;
using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameController.FBServiceExt;
using GameController.FBServiceExt.Application;
using GameController.FBServiceExt.Application.Abstractions.State;
using GameController.FBServiceExt.Application.Abstractions.Observability;
using GameController.FBServiceExt.Application.Contracts.Observability;
using GameController.FBServiceExt.Application.Contracts.Runtime;
using GameController.FBServiceExt.DevAdmin;
using GameController.FBServiceExt.DevLogs;
using GameController.FBServiceExt.DevMetrics;
using GameController.FBServiceExt.Infrastructure;
using GameController.FBServiceExt.Infrastructure.Logging;
using GameController.FBServiceExt.Infrastructure.Messaging;
using GameController.FBServiceExt.Options;
using GameController.FBServiceExt.Startup;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
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

    var environmentName = builder.Environment.EnvironmentName;
    builder.Configuration.Sources.Clear();
    AddSharedJsonFiles(builder.Configuration, builder.Environment.ContentRootPath, environmentName);
    builder.Configuration
        .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
        .AddJsonFile($"appsettings.{environmentName}.json", optional: true, reloadOnChange: true)
        .AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true)
        .AddEnvironmentVariables();

    if (args is { Length: > 0 })
    {
        builder.Configuration.AddCommandLine(args);
    }
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
    builder.Services.AddOptions<LocalWorkerControlOptions>()
        .Bind(builder.Configuration.GetSection(LocalWorkerControlOptions.SectionName));
    builder.Services.AddSingleton<ILocalWorkerProcessFactory, SystemLocalWorkerProcessFactory>();
    builder.Services.AddSingleton<LocalWorkerManager>();
    builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(options =>
        {
            options.Cookie.Name = builder.Configuration.GetValue<string>($"{AdminPortalOptions.SectionName}:CookieName")
                                  ?? AdminPortalOptions.DefaultCookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.SlidingExpiration = true;
            options.ExpireTimeSpan = TimeSpan.FromMinutes(
                Math.Max(
                    5,
                    builder.Configuration.GetValue<int?>($"{AdminPortalOptions.SectionName}:SessionIdleTimeoutMinutes")
                    ?? AdminPortalOptions.DefaultSessionIdleTimeoutMinutes));
            options.Events = new CookieAuthenticationEvents
            {
                OnRedirectToLogin = context =>
                {
                    if (context.Request.Path.StartsWithSegments("/admin/api", StringComparison.OrdinalIgnoreCase))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    }

                    context.Response.Redirect("/admin");
                    return Task.CompletedTask;
                },
                OnRedirectToAccessDenied = context =>
                {
                    if (context.Request.Path.StartsWithSegments("/admin/api", StringComparison.OrdinalIgnoreCase))
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return Task.CompletedTask;
                    }

                    context.Response.Redirect("/admin");
                    return Task.CompletedTask;
                }
            };
        });
    builder.Services.AddAuthorization();
    builder.Services.AddOptions<AdminPortalOptions>()
        .Bind(builder.Configuration.GetSection(AdminPortalOptions.SectionName))
        .Validate(static options =>
                !string.IsNullOrWhiteSpace(options.Username) &&
                !string.IsNullOrWhiteSpace(options.Password),
            "AdminPortal username and password are required.")
        .ValidateOnStart();
    builder.Services.AddOptions<DevLogViewerOptions>()
        .Bind(builder.Configuration.GetSection(DevLogViewerOptions.SectionName));
    builder.Services.AddHttpClient<GraylogLogViewerService>(client => client.Timeout = TimeSpan.FromSeconds(5));
    builder.Services.AddHttpClient<GraylogLogCleanupService>(client => client.Timeout = TimeSpan.FromSeconds(30));
    builder.Services.AddHostedService<LocalDevBrowserTabsHostedService>();
    builder.Services.AddIngressApplication(builder.Configuration);
    builder.Services.AddIngressInfrastructure(builder.Configuration);

    var app = builder.Build();

    var devLogViewerOptions = app.Services.GetRequiredService<IOptionsMonitor<DevLogViewerOptions>>();
    if (devLogViewerOptions.CurrentValue.ClearLogsOnStart)
    {
        using var startupScope = app.Services.CreateScope();
        var graylogLogCleanupService = startupScope.ServiceProvider.GetRequiredService<GraylogLogCleanupService>();

        try
        {
            var summary = await graylogLogCleanupService.ClearLogsAsync(CancellationToken.None);
            Log.Information(
                "Graylog startup cleanup completed. IndexSetsDiscovered: {IndexSetsDiscovered}, IndexSetsCycled: {IndexSetsCycled}, DeletedIndices: {DeletedIndices}",
                summary.IndexSetsDiscovered,
                summary.IndexSetsCycled,
                summary.DeletedIndices);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Graylog startup cleanup failed.");
        }
    }

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
                path.StartsWithSegments("/dev-metrics", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWithSegments("/dev/admin", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWithSegments("/dev-admin", StringComparison.OrdinalIgnoreCase))
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
    app.UseAuthentication();
    app.UseAuthorization();
    app.Use(async (context, next) =>
    {
        if ((context.Request.Path.StartsWithSegments("/dev/logs", StringComparison.OrdinalIgnoreCase) ||
             context.Request.Path.StartsWithSegments("/dev-logs", StringComparison.OrdinalIgnoreCase) ||
             context.Request.Path.StartsWithSegments("/dev/metrics", StringComparison.OrdinalIgnoreCase) ||
             context.Request.Path.StartsWithSegments("/dev-metrics", StringComparison.OrdinalIgnoreCase) ||
             context.Request.Path.StartsWithSegments("/dev/admin", StringComparison.OrdinalIgnoreCase) ||
             context.Request.Path.StartsWithSegments("/dev-admin", StringComparison.OrdinalIgnoreCase) ||
             context.Request.Path.StartsWithSegments("/dev/votes", StringComparison.OrdinalIgnoreCase) ||
             context.Request.Path.StartsWithSegments("/dev-votes", StringComparison.OrdinalIgnoreCase)) &&
            !IsLocalRequest(context))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await next();
    });
    app.UseStaticFiles();

    app.MapGet("/admin", () => Results.Redirect("/admin/index.html"));
    app.MapGet("/admin/api/session", (HttpContext context) =>
    {
        var identity = context.User.Identity;
        if (identity is null || !identity.IsAuthenticated)
        {
            return Results.Ok(new
            {
                authenticated = false,
                username = string.Empty
            });
        }

        return Results.Ok(new
        {
            authenticated = true,
            username = context.User.FindFirstValue(ClaimTypes.Name) ?? identity.Name ?? string.Empty
        });
    });
    app.MapPost("/admin/api/login", async (
        AdminLoginRequest request,
        HttpContext context,
        IOptionsMonitor<AdminPortalOptions> optionsMonitor) =>
    {
        var options = optionsMonitor.CurrentValue;
        if (!FixedTimeEquals(request.Username, options.Username) || !FixedTimeEquals(request.Password, options.Password))
        {
            return Results.Json(
                new
                {
                    authenticated = false,
                    error = "Invalid username or password."
                },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, options.Username),
            new Claim(ClaimTypes.Role, "Operator")
        };

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

        return Results.Ok(new
        {
            authenticated = true,
            username = options.Username
        });
    });
    app.MapPost("/admin/api/logout", async (HttpContext context) =>
    {
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.Ok(new
        {
            authenticated = false
        });
    });

    var adminApi = app.MapGroup("/admin/api").RequireAuthorization();
    adminApi.MapGet("/dashboard", async (
        IVotingGateService votingGateService,
        DevMetricsDashboardService dashboardService,
        HttpContext context,
        CancellationToken cancellationToken) =>
    {
        var state = await votingGateService.GetStateAsync(cancellationToken);
        var snapshot = await dashboardService.GetSnapshotAsync(cancellationToken);

        return Results.Ok(new AdminDashboardResponse(
            VotingStarted: state.VotingStarted,
            ActiveShowId: state.ActiveShowId,
            Utc: DateTime.UtcNow,
            Operator: context.User.FindFirstValue(ClaimTypes.Name) ?? string.Empty,
            Source: "redis",
            Metrics: snapshot));
    });
    adminApi.MapPut("/voting", async (
        VotingGateUpdateRequest request,
        IVotingGateService votingGateService,
        DevMetricsDashboardService dashboardService,
        HttpContext context,
        CancellationToken cancellationToken) =>
    {
        var updatedState = new VotingRuntimeState(request.VotingStarted, string.IsNullOrWhiteSpace(request.ActiveShowId) ? null : request.ActiveShowId.Trim());
        await votingGateService.SetStateAsync(updatedState, cancellationToken);
        var snapshot = await dashboardService.GetSnapshotAsync(cancellationToken);

        return Results.Ok(new AdminDashboardResponse(
            VotingStarted: updatedState.VotingStarted,
            ActiveShowId: updatedState.ActiveShowId,
            Utc: DateTime.UtcNow,
            Operator: context.User.FindFirstValue(ClaimTypes.Name) ?? string.Empty,
            Source: "redis",
            Metrics: snapshot));
    });

    if (app.Environment.IsEnvironment("PerformanceFakeFb"))
    {
        app.MapDelete("/fake-meta/api/messages", async (RedisFakeMetaMessengerStore store, CancellationToken cancellationToken) =>
        {
            await store.ClearAsync(cancellationToken);
            return Results.NoContent();
        });

        app.MapGet("/fake-meta/api/recipients/{recipientId}/messages", async (
            string recipientId,
            long? afterSequence,
            int? waitSeconds,
            RedisFakeMetaMessengerStore store,
            CancellationToken cancellationToken) =>
        {
            var safeWaitSeconds = Math.Clamp(waitSeconds ?? 0, 0, 30);
            var messages = safeWaitSeconds > 0
                ? await store.WaitForMessagesAsync(recipientId, afterSequence ?? 0, TimeSpan.FromSeconds(safeWaitSeconds), cancellationToken)
                : await store.GetMessagesAsync(recipientId, afterSequence ?? 0, cancellationToken);
            return Results.Ok(messages);
        });

        app.MapPost("/fake-meta/{version}/me/messages", async (string version, HttpRequest request, RedisFakeMetaMessengerStore store, CancellationToken cancellationToken) =>
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("recipient", out var recipient) ||
                recipient.ValueKind != JsonValueKind.Object ||
                !recipient.TryGetProperty("id", out var recipientIdElement) ||
                recipientIdElement.ValueKind != JsonValueKind.String)
            {
                return Results.BadRequest(new { error = "recipient.id is required" });
            }

            var recipientId = recipientIdElement.GetString() ?? string.Empty;
            var captured = await store.CaptureRequestAsync(recipientId, version, document, cancellationToken);
            return Results.Ok(new
            {
                recipient_id = recipientId,
                message_id = $"fake-meta.{captured.Sequence}"
            });
        });
    }
    var devToolsEnabled = app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Performance") || app.Environment.IsEnvironment("Simulator") || app.Environment.IsEnvironment("PerformanceFakeFb") || devLogViewerOptions.CurrentValue.Enabled;
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

        app.MapGet("/dev/votes", () => Results.Redirect("/dev-votes/index.html"));
        app.MapGet("/dev/votes/api", async (
            DateTime? fromUtc,
            DateTime? toUtc,
            string? showId,
            string? userFilter,
            int? page,
            int? pageSize,
            IVotingGateService votingGateService,
            IAcceptedVotesMonitorService monitorService,
            CancellationToken cancellationToken) =>
        {
            var effectiveShowId = string.IsNullOrWhiteSpace(showId)
                ? await votingGateService.GetActiveShowIdAsync(cancellationToken)
                : showId.Trim();
            var snapshot = await monitorService.GetSnapshotAsync(
                fromUtc,
                toUtc,
                effectiveShowId,
                userFilter,
                page ?? 1,
                pageSize ?? 200,
                cancellationToken);
            return Results.Ok(new
            {
                generatedAtUtc = snapshot.GeneratedAtUtc,
                fromUtc = snapshot.FromUtc,
                toUtc = snapshot.ToUtc,
                showId = snapshot.ShowId,
                totalVotes = snapshot.TotalVotes,
                totalUniqueUsers = snapshot.TotalUniqueUsers,
                candidates = snapshot.Candidates,
                recentVotesPage = snapshot.RecentVotesPage,
                source = string.IsNullOrWhiteSpace(showId) ? "active-show" : "explicit-show"
            });
        });

        app.MapGet("/dev/admin", () => Results.Redirect("/dev-admin/index.html"));
        app.MapGet("/dev/admin/api/voting", async (IVotingGateService votingGateService, CancellationToken cancellationToken) =>
        {
            var state = await votingGateService.GetStateAsync(cancellationToken);
            return Results.Ok(new
            {
                votingStarted = state.VotingStarted,
                activeShowId = state.ActiveShowId,
                utc = DateTime.UtcNow,
                source = "redis"
            });
        });
        app.MapPut("/dev/admin/api/voting", async (VotingGateUpdateRequest request, IVotingGateService votingGateService, CancellationToken cancellationToken) =>
        {
            var updatedState = new VotingRuntimeState(request.VotingStarted, string.IsNullOrWhiteSpace(request.ActiveShowId) ? null : request.ActiveShowId.Trim());
            await votingGateService.SetStateAsync(updatedState, cancellationToken);
            return Results.Ok(new
            {
                votingStarted = updatedState.VotingStarted,
                activeShowId = updatedState.ActiveShowId,
                utc = DateTime.UtcNow,
                source = "redis"
            });
        });
        app.MapGet("/dev/admin/api/workers", async (LocalWorkerManager workerManager, CancellationToken cancellationToken) =>
        {
            try
            {
                var snapshot = await workerManager.GetSnapshotAsync(cancellationToken);
                return Results.Ok(snapshot);
            }
            catch (Exception exception)
            {
                return Results.Problem(
                    detail: exception.Message,
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "Worker snapshot failed.");
            }
        });
        app.MapPut("/dev/admin/api/workers", async (WorkerCountUpdateRequest request, LocalWorkerManager workerManager, CancellationToken cancellationToken) =>
        {
            try
            {
                var snapshot = await workerManager.EnsureWorkerCountAsync(request.ManagedWorkerCount, cancellationToken);
                return Results.Ok(snapshot);
            }
            catch (Exception exception)
            {
                return Results.Problem(
                    detail: exception.Message,
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "Worker update failed.");
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

static bool FixedTimeEquals(string? left, string? right)
{
    var leftBytes = Encoding.UTF8.GetBytes(left ?? string.Empty);
    var rightBytes = Encoding.UTF8.GetBytes(right ?? string.Empty);

    return leftBytes.Length == rightBytes.Length &&
           CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
}

static void AddSharedJsonFiles(IConfigurationBuilder configurationBuilder, string contentRootPath, string environmentName)
{
    foreach (var sharedPath in ResolveSharedConfigPaths(contentRootPath, environmentName))
    {
        configurationBuilder.AddJsonFile(sharedPath, optional: false, reloadOnChange: true);
    }
}
static IEnumerable<string> ResolveSharedConfigPaths(string contentRootPath, string environmentName)
{
    var candidateRoots = new[]
    {
        contentRootPath,
        Path.GetFullPath(Path.Combine(contentRootPath, "..", ".."))
    };
    foreach (var root in candidateRoots.Distinct(StringComparer.OrdinalIgnoreCase))
    {
        var shared = Path.Combine(root, "appsettings.Shared.json");
        if (!File.Exists(shared))
        {
            continue;
        }
        yield return shared;
        var environmentShared = Path.Combine(root, $"appsettings.Shared.{environmentName}.json");
        if (File.Exists(environmentShared))
        {
            yield return environmentShared;
        }
        yield break;
    }
}

internal sealed record AdminLoginRequest(string Username, string Password);
internal sealed record VotingGateUpdateRequest(bool VotingStarted, string? ActiveShowId);
internal sealed record AdminDashboardResponse(
    bool VotingStarted,
    string? ActiveShowId,
    DateTime Utc,
    string Operator,
    string Source,
    RuntimeMetricsDashboardSnapshot Metrics);














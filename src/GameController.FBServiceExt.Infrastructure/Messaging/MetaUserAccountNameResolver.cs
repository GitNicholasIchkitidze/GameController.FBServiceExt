using System.Net;
using System.Text.Json;
using GameController.FBServiceExt.Application.Abstractions.Observability;
using GameController.FBServiceExt.Application.Abstractions.Processing;
using GameController.FBServiceExt.Application.Abstractions.State;
using GameController.FBServiceExt.Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameController.FBServiceExt.Infrastructure.Messaging;

internal sealed class MetaUserAccountNameResolver : IUserAccountNameResolver
{
    private readonly HttpClient _httpClient;
    private readonly IUserAccountNameStore _userAccountNameStore;
    private readonly IOptionsMonitor<MetaMessengerOptions> _optionsMonitor;
    private readonly IRuntimeMetricsCollector _runtimeMetricsCollector;
    private readonly ILogger<MetaUserAccountNameResolver> _logger;

    public MetaUserAccountNameResolver(
        HttpClient httpClient,
        IUserAccountNameStore userAccountNameStore,
        IOptionsMonitor<MetaMessengerOptions> optionsMonitor,
        IRuntimeMetricsCollector runtimeMetricsCollector,
        ILogger<MetaUserAccountNameResolver> logger)
    {
        _httpClient = httpClient;
        _userAccountNameStore = userAccountNameStore;
        _optionsMonitor = optionsMonitor;
        _runtimeMetricsCollector = runtimeMetricsCollector;
        _logger = logger;
    }

    // Facebook user display name-ს ჯერ cache-დან ეძებს, მერე საჭირო Meta API-დან resolve-ს ცდილობს.
    public async ValueTask<string?> GetOrResolveAsync(string userId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        var cached = await _userAccountNameStore.GetAsync(userId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(cached))
        {
            _runtimeMetricsCollector.Increment("worker.user_account_name.cache_hit");
            return cached;
        }

        _runtimeMetricsCollector.Increment("worker.user_account_name.cache_miss");

        var options = _optionsMonitor.CurrentValue;
        if (IsSimulatorRecipientId(userId) || options.UseFakeMetaStoreClient || options.UseNoOpClient || IsLoopbackBaseUrl(options.GraphApiBaseUrl))
        {
            var simulatedName = userId;
            await _userAccountNameStore.SetAsync(userId, simulatedName, options.UserAccountNameCacheTtl, cancellationToken);
            _runtimeMetricsCollector.Increment("worker.user_account_name.lookup_skipped_simulator");
            return simulatedName;
        }

        if (string.IsNullOrWhiteSpace(options.PageAccessToken))
        {
            return await CacheFallbackAsync(userId, options, "worker.user_account_name.lookup_skipped_missing_token", cancellationToken);
        }

        var version = string.IsNullOrWhiteSpace(options.GraphApiVersion)
            ? "v24.0"
            : options.GraphApiVersion.Trim();
        var baseUrl = ResolveUserProfileBaseUrl(options);
        var requestUri = $"{baseUrl}/{version}/{Uri.EscapeDataString(userId)}?fields=first_name,last_name&access_token={Uri.EscapeDataString(options.PageAccessToken)}";

        try
        {
            using var response = await _httpClient.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _runtimeMetricsCollector.Increment("worker.user_account_name.lookup_failed");
                _runtimeMetricsCollector.Increment($"worker.user_account_name.lookup_status.{(int)response.StatusCode}");
                _logger.LogWarning(
                    "User account name lookup failed. UserId: {UserId}, StatusCode: {StatusCode}",
                    userId,
                    (int)response.StatusCode);
                return await CacheFallbackAsync(userId, options, "worker.user_account_name.lookup_fallback", cancellationToken);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            var firstName = root.TryGetProperty("first_name", out var firstNameElement) && firstNameElement.ValueKind == JsonValueKind.String
                ? firstNameElement.GetString()
                : null;
            var lastName = root.TryGetProperty("last_name", out var lastNameElement) && lastNameElement.ValueKind == JsonValueKind.String
                ? lastNameElement.GetString()
                : null;
            var displayName = BuildDisplayName(firstName, lastName);
            if (string.IsNullOrWhiteSpace(displayName))
            {
                _runtimeMetricsCollector.Increment("worker.user_account_name.lookup_empty");
                return await CacheFallbackAsync(userId, options, "worker.user_account_name.lookup_empty_fallback", cancellationToken);
            }

            await _userAccountNameStore.SetAsync(userId, displayName, options.UserAccountNameCacheTtl, cancellationToken);
            _runtimeMetricsCollector.Increment("worker.user_account_name.lookup_success");
            return displayName;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _runtimeMetricsCollector.Increment("worker.user_account_name.lookup_failed");
            _runtimeMetricsCollector.Increment($"worker.user_account_name.lookup_exception.{SanitizeMetricSegment(exception.GetType().Name)}");
            _logger.LogWarning(exception, "User account name lookup threw an exception. UserId: {UserId}", userId);
            return await CacheFallbackAsync(userId, options, "worker.user_account_name.lookup_exception_fallback", cancellationToken);
        }
    }

    internal static string ResolveUserProfileBaseUrl(MetaMessengerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var configuredBaseUrl = string.IsNullOrWhiteSpace(options.GraphApiBaseUrl)
            ? "https://graph.facebook.com"
            : options.GraphApiBaseUrl.Trim().TrimEnd('/');

        if (IsLoopbackBaseUrl(configuredBaseUrl))
        {
            return "https://graph.facebook.com";
        }

        return configuredBaseUrl;
    }

    internal static bool IsSimulatorRecipientId(string userId)
        => !string.IsNullOrWhiteSpace(userId) && userId.StartsWith("simulate-user-", StringComparison.OrdinalIgnoreCase);

    private async ValueTask<string> CacheFallbackAsync(string userId, MetaMessengerOptions options, string metricName, CancellationToken cancellationToken)
    {
        var fallback = userId + " NOT FETCHED FROM FB";
        await _userAccountNameStore.SetAsync(userId, fallback, options.UserAccountNameCacheTtl, cancellationToken);
        _runtimeMetricsCollector.Increment(metricName);
        return fallback;
    }

    private static string? BuildDisplayName(string? firstName, string? lastName)
    {
        var fullName = $"{firstName} {lastName}".Trim();
        return string.IsNullOrWhiteSpace(fullName) ? null : fullName;
    }

    private static bool IsLoopbackBaseUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(uri.Host, out var ipAddress) && IPAddress.IsLoopback(ipAddress);
    }

    private static string SanitizeMetricSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        Span<char> buffer = stackalloc char[value.Length];
        var index = 0;
        foreach (var character in value)
        {
            buffer[index++] = char.IsLetterOrDigit(character)
                ? char.ToLowerInvariant(character)
                : '_';
        }

        return new string(buffer[..index]);
    }
}

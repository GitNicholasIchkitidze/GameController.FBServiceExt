namespace GameController.FBServiceExt.FakeFBForSimulate;

internal sealed class SimulatorApiPreflightClient : IDisposable
{
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    public async Task<SimulatorApiPreflightResult> CheckAsync(string webhookUrl, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var webhookUri))
        {
            return new SimulatorApiPreflightResult(false, "Webhook URL is invalid.", false, "Webhook URL is invalid.");
        }

        var root = new Uri($"{webhookUri.Scheme}://{webhookUri.Authority}");
        var healthLiveUri = new Uri(root, "/health/live");
        var devVotingUri = new Uri(root, "/dev/admin/api/voting");

        var (healthOk, healthDetail) = await CheckEndpointAsync(healthLiveUri, cancellationToken).ConfigureAwait(false);
        var (votingOk, votingDetail) = await CheckEndpointAsync(devVotingUri, cancellationToken).ConfigureAwait(false);

        return new SimulatorApiPreflightResult(healthOk, healthDetail, votingOk, votingDetail);
    }

    public void Dispose() => _httpClient.Dispose();

    private async Task<(bool ok, string detail)> CheckEndpointAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return (true, $"HTTP {(int)response.StatusCode}");
            }

            return (false, $"HTTP {(int)response.StatusCode}: {Trim(body)}");
        }
        catch (Exception exception)
        {
            return (false, Trim(exception.Message));
        }
    }

    private static string Trim(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "No detail.";
        }

        var compact = value.ReplaceLineEndings(" ").Trim();
        return compact.Length <= 220 ? compact : compact[..220];
    }
}

internal sealed record SimulatorApiPreflightResult(
    bool HealthLiveOk,
    string HealthLiveDetail,
    bool DevAdminVotingOk,
    string DevAdminVotingDetail);

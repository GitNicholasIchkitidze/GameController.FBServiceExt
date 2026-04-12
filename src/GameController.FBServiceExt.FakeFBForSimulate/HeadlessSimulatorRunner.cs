using System.Globalization;

namespace GameController.FBServiceExt.FakeFBForSimulate;

internal static class HeadlessSimulatorRunner
{
    public static async Task<int> RunAsync(SimulatorDefaults defaults, string[] args)
    {
        var settings = BuildSettings(defaults, args);
        await using var engine = new FakeFacebookSimulatorEngine(defaults);
        engine.LogProduced += static message => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

        Console.WriteLine("FakeFBForSimulate headless run starting...");
        Console.WriteLine($"WebhookUrl={settings.WebhookUrl}");
        Console.WriteLine($"FakeMetaMode={FakeFacebookSimulatorEngine.FakeMetaTransportMode}");
        Console.WriteLine($"Users={settings.UserCount}, DurationSeconds={settings.DurationSeconds}, CooldownSeconds={settings.CooldownSeconds}");

        await engine.StartAsync(settings).ConfigureAwait(false);

        var totalWait = TimeSpan.FromSeconds(settings.DurationSeconds + settings.CooldownSeconds + settings.OutboundWaitSeconds + 30);
        var startedAt = DateTimeOffset.UtcNow;
        var nextPrintAt = startedAt;
        var completedNaturally = false;

        while (DateTimeOffset.UtcNow - startedAt < totalWait)
        {
            var now = DateTimeOffset.UtcNow;
            var snapshot = engine.GetSnapshot();
            if (now >= nextPrintAt)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Snapshot: active={snapshot.ActiveUsers}, completed={snapshot.CyclesCompleted}, failed={snapshot.CyclesFailed}, webhooks={snapshot.WebhookSuccesses}/{snapshot.WebhookAttempts}, outbound={snapshot.OutboundMessagesReceived}");
                nextPrintAt = now.AddSeconds(5);
            }

            if (!snapshot.IsRunning && snapshot.ActiveUsers == 0)
            {
                completedNaturally = true;
                break;
            }

            await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
        }

        if (!completedNaturally)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Runner deadline reached before all fake users finished.");
        }

        await engine.StopAsync().ConfigureAwait(false);
        var finalSnapshot = engine.GetSnapshot();
        Console.WriteLine(
            "FinalSnapshot: " +
            $"active={finalSnapshot.ActiveUsers}, " +
            $"started={finalSnapshot.CyclesStarted}, " +
            $"completed={finalSnapshot.CyclesCompleted}, " +
            $"failed={finalSnapshot.CyclesFailed}, " +
            $"webhookAttempts={finalSnapshot.WebhookAttempts}, " +
            $"webhookSuccesses={finalSnapshot.WebhookSuccesses}, " +
            $"webhookFailures={finalSnapshot.WebhookFailures}, " +
            $"outbound={finalSnapshot.OutboundMessagesReceived}, " +
            $"acceptedTexts={finalSnapshot.AcceptedTextsReceived}, " +
            $"unexpectedShapes={finalSnapshot.UnexpectedOutboundShapes}, " +
            $"averageCompletedCycleMs={finalSnapshot.AverageCompletedCycleMilliseconds:F2}");

        return finalSnapshot.CyclesCompleted > 0 && finalSnapshot.WebhookFailures == 0 ? 0 : 1;
    }

    private static SimulatorRunSettings BuildSettings(SimulatorDefaults defaults, string[] args)
    {
        var values = ParseArgs(args);
        var minThink = GetInt(values, "min-think-ms", defaults.DefaultMinThinkMilliseconds);
        var maxThink = GetInt(values, "max-think-ms", defaults.DefaultMaxThinkMilliseconds);
        if (maxThink < minThink)
        {
            throw new InvalidOperationException("--max-think-ms must be greater than or equal to --min-think-ms.");
        }

        return new SimulatorRunSettings(
            GetString(values, "webhook-url", defaults.WebhookUrl),
            GetString(values, "listener-url", defaults.ListenerUrl),
            GetString(values, "page-id", defaults.PageId),
            GetString(values, "app-secret", defaults.AppSecret),
            GetString(values, "start-token", defaults.StartToken),
            GetInt(values, "users", defaults.DefaultUserCount),
            GetInt(values, "duration-seconds", defaults.DefaultDurationSeconds),
            GetInt(values, "cooldown-seconds", defaults.DefaultCooldownSeconds),
            GetInt(values, "startup-jitter-seconds", defaults.DefaultStartupJitterSeconds),
            minThink,
            maxThink,
            GetInt(values, "outbound-wait-seconds", defaults.DefaultOutboundWaitSeconds),
            GetInt(values, "failure-backoff-min-seconds", defaults.DefaultFailureBackoffMinSeconds),
            GetInt(values, "failure-backoff-max-seconds", defaults.DefaultFailureBackoffMaxSeconds),
            GetString(values, "active-show-id", defaults.ActiveShowId),
            GetBool(values, "configure-voting-gate-on-start", defaults.ConfigureVotingGateOnStart),
            new SimulatorTextPatterns(
                defaults.CooldownTextFragments,
                defaults.RejectedTextFragments,
                defaults.ExpiredTextFragments,
                defaults.InactiveVotingTextFragments));
    }

    private static Dictionary<string, string> ParseArgs(IEnumerable<string> args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? pendingKey = null;
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--headless", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                pendingKey = arg[2..];
                values[pendingKey] = string.Empty;
                continue;
            }

            if (pendingKey is not null)
            {
                values[pendingKey] = arg;
                pendingKey = null;
            }
        }

        return values;
    }

    private static string GetString(IReadOnlyDictionary<string, string> values, string key, string fallback)
        => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

    private static int GetInt(IReadOnlyDictionary<string, string> values, string key, int fallback)
    {
        if (values.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private static bool GetBool(IReadOnlyDictionary<string, string> values, string key, bool fallback)
    {
        if (values.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        return fallback;
    }
}



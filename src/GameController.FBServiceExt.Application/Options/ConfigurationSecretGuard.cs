namespace GameController.FBServiceExt.Application.Options;

public static class ConfigurationSecretGuard
{
    private static readonly string[] PlaceholderFragments =
    [
        "change-me",
        "placeholder",
        "example",
        "sample",
        "todo",
        "set-me",
        "replace-me",
        "your-",
        "dummy"
    ];

    public static bool HasUsableSecret(string? value)
        => !LooksPlaceholder(value);

    public static bool LooksPlaceholder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var trimmed = value.Trim();
        var normalized = trimmed.ToLowerInvariant();

        if (normalized is "guest" or "fbvote" or "fake-meta-token")
        {
            return true;
        }

        if (normalized.StartsWith("__") || normalized.EndsWith("__"))
        {
            return true;
        }

        foreach (var fragment in PlaceholderFragments)
        {
            if (normalized.Contains(fragment, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
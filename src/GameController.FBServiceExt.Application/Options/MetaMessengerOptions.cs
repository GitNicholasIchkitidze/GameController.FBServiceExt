namespace GameController.FBServiceExt.Application.Options;

public sealed class MetaMessengerOptions
{
    public const string SectionName = "MetaMessenger";
    public const string DefaultSimulatorGraphApiBaseUrl = "http://127.0.0.1:5290";

    public bool Enabled { get; set; }

    public bool UseNoOpClient { get; set; }

    public bool UseFakeMetaStoreClient { get; set; }

    public string PageAccessToken { get; set; } = string.Empty;

    public string GraphApiVersion { get; set; } = "v24.0";

    public string GraphApiBaseUrl { get; set; } = string.Empty;

    public string SimulatorGraphApiBaseUrl { get; set; } = DefaultSimulatorGraphApiBaseUrl;

    public TimeSpan UserAccountNameCacheTtl { get; set; } = TimeSpan.FromDays(7);
}

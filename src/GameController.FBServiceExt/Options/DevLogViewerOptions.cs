namespace GameController.FBServiceExt.Options;

public sealed class DevLogViewerOptions
{
    public const string SectionName = "DevLogViewer";

    public bool Enabled { get; set; }

    public string GraylogBaseUrl { get; set; } = "http://127.0.0.1:9000";

    public string Username { get; set; } = "admin";

    public string Password { get; set; } = string.Empty;

    public string DefaultQuery { get; set; } = "Application:GameController.FBServiceExt";

    public int DefaultLimit { get; set; } = 100;

    public int MaxLimit { get; set; } = 250;
}
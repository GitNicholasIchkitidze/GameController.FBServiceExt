namespace GameController.FBServiceExt.Options;

public sealed class AdminPortalOptions
{
    public const string SectionName = "AdminPortal";
    public const string DefaultCookieName = "fbserviceext-admin";
    public const int DefaultSessionIdleTimeoutMinutes = 480;

    public string Username { get; set; } = "operator";

    public string Password { get; set; } = "change-me-admin-password";

    public string CookieName { get; set; } = DefaultCookieName;

    public int SessionIdleTimeoutMinutes { get; set; } = DefaultSessionIdleTimeoutMinutes;
}

namespace GameController.FBServiceExt.Application.Options;

public sealed class WebhookIngressOptions
{
    public const string SectionName = "WebhookIngress";

    public string Source { get; set; } = "facebook-messenger";

    public int MaxRequestBodySizeBytes { get; set; } = 262_144;
}

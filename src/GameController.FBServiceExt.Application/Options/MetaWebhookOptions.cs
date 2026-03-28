namespace GameController.FBServiceExt.Application.Options;

public sealed class MetaWebhookOptions
{
    public const string SectionName = "MetaWebhook";

    public string VerifyToken { get; set; } = string.Empty;

    public string AppSecret { get; set; } = string.Empty;

    public bool RequireSignatureValidation { get; set; } = true;
}

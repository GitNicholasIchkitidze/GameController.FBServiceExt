namespace GameController.FBServiceExt.Application.Options;

public sealed class DataErasureOptions
{
    public const string SectionName = "DataErasure";

    public string ConfirmationPayloadSecret { get; set; } = string.Empty;

    public TimeSpan ConfirmationTimeout { get; set; } = TimeSpan.FromMinutes(2);
}

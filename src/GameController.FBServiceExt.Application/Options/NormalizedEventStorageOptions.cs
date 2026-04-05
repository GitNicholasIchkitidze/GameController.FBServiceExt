namespace GameController.FBServiceExt.Application.Options;
public sealed class NormalizedEventStorageOptions
{
    public const string SectionName = "NormalizedEventStorage";
    public NormalizedEventStorageMode Mode { get; set; } = NormalizedEventStorageMode.Full;
}
public enum NormalizedEventStorageMode
{
    Full = 0,
    Minimal = 1,
    Disabled = 2
}
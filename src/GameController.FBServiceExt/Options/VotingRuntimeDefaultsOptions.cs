namespace GameController.FBServiceExt.Options;

public sealed class VotingRuntimeDefaultsOptions
{
    public const string SectionName = "VotingRuntimeDefaults";

    public bool ApplyDefaultActiveShowIdWhenMissing { get; set; }

    public string? DefaultActiveShowId { get; set; }
}

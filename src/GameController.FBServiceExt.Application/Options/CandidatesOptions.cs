namespace GameController.FBServiceExt.Application.Options;

public sealed class CandidatesOptions
{
    public const string SectionName = "Candidates";

    public string PublicBaseUrl { get; set; } = string.Empty;

    public string AssetBasePath { get; set; } = "assets/L1";

    public List<CandidateDefinition> Items { get; set; } = new();
}

public sealed class CandidateDefinition
{
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Image { get; set; } = string.Empty;

    public string? Subtitle { get; set; }

    public string? Phone { get; set; }

    public string? ButtonTitle { get; set; }

    public bool Enabled { get; set; } = true;
}

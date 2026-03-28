namespace GameController.FBServiceExt.Application.Options;

public sealed class CandidatesOptions
{
    public const string SectionName = "Candidates";

    public List<CandidateDefinition> Items { get; set; } = new();
}

public sealed class CandidateDefinition
{
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
}

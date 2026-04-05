using GameController.FBServiceExt.Domain.Voting;

namespace GameController.FBServiceExt.Application.Options;

public sealed class VotingWorkflowOptions
{
    public const string SectionName = "VotingWorkflow";

    public TimeSpan ConfirmationTimeout { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan Cooldown { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan ProcessedEventRetention { get; set; } = TimeSpan.FromHours(24);

    public TimeSpan ProcessingLockTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public bool RequireConfirmationForAll { get; set; } = true;

    public string PayloadSignatureSecret { get; set; } = string.Empty;

    public List<string> VoteStartTokens { get; set; } = new() { "GET_STARTED", "_voteStartFlag" };

    public CooldownResponseMode CooldownResponseMode { get; set; } = CooldownResponseMode.Message;

    public bool IncludeRemainingCooldownTime { get; set; } = true;
}

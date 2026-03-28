using GameController.FBServiceExt.Domain.Voting;

namespace GameController.FBServiceExt.Application.Options;

public sealed class VotingWorkflowOptions
{
    public const string SectionName = "VotingWorkflow";

    public TimeSpan OptionsSessionTtl { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan ConfirmationTimeout { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan Cooldown { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan ProcessedEventRetention { get; set; } = TimeSpan.FromHours(24);

    public TimeSpan ProcessingLockTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public List<string> VoteStartTokens { get; set; } = new() { "GET_STARTED", "_voteStartFlag" };

    public List<string> ConfirmationAcceptTokens { get; set; } = new() { "YES", "YESCONFIRMED", "CONFIRM:YES" };

    public List<string> ConfirmationRejectTokens { get; set; } = new() { "NO", "NOCONFIRMED", "CONFIRM:NO" };

    public ExpirationBehavior OptionsExpirationBehavior { get; set; } = ExpirationBehavior.Silent;

    public ExpirationBehavior ConfirmationExpirationBehavior { get; set; } = ExpirationBehavior.Silent;

    public CooldownResponseMode CooldownResponseMode { get; set; } = CooldownResponseMode.Message;

    public bool IncludeRemainingCooldownTime { get; set; } = true;
}

namespace GameController.FBServiceExt.Domain.Voting;

public enum VoteState
{
    Idle = 0,
    VoteRequested = 1,
    OptionsSent = 2,
    CandidateSelected = 3,
    ConfirmationPending = 4,
    ConfirmationReceived = 5,
    VotePersisted = 6,
    UserNotified = 7,
    CooldownActive = 8,
    Expired = 9,
    Rejected = 10
}

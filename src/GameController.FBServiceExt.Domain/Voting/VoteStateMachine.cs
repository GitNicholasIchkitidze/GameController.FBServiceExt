namespace GameController.FBServiceExt.Domain.Voting;

public static class VoteStateMachine
{
    public static bool CanTransition(VoteState current, VoteState next)
    {
        return current switch
        {
            VoteState.Idle => next is VoteState.VoteRequested,
            VoteState.VoteRequested => next is VoteState.OptionsSent or VoteState.Rejected,
            VoteState.OptionsSent => next is VoteState.CandidateSelected or VoteState.Expired or VoteState.Rejected,
            VoteState.CandidateSelected => next is VoteState.ConfirmationPending or VoteState.Rejected,
            VoteState.ConfirmationPending => next is VoteState.ConfirmationReceived or VoteState.Expired or VoteState.Rejected,
            VoteState.ConfirmationReceived => next is VoteState.VotePersisted or VoteState.Rejected,
            VoteState.VotePersisted => next is VoteState.UserNotified,
            VoteState.UserNotified => next is VoteState.CooldownActive,
            VoteState.CooldownActive => next is VoteState.Idle,
            VoteState.Expired => next is VoteState.Idle,
            VoteState.Rejected => next is VoteState.Idle,
            _ => false
        };
    }

    public static VoteState Transition(VoteState current, VoteState next)
    {
        if (!CanTransition(current, next))
        {
            throw new InvalidOperationException($"Invalid vote state transition from {current} to {next}.");
        }

        return next;
    }
}

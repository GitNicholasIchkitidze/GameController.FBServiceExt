using GameController.FBServiceExt.Domain.Voting;

namespace GameController.FBServiceExt.Tests.Domain;

public sealed class VoteStateMachineTests
{
    [Fact]
    public void HappyPathTransitions_AreAllowed()
    {
        var transitions = new[]
        {
            (VoteState.Idle, VoteState.VoteRequested),
            (VoteState.VoteRequested, VoteState.OptionsSent),
            (VoteState.OptionsSent, VoteState.CandidateSelected),
            (VoteState.CandidateSelected, VoteState.ConfirmationPending),
            (VoteState.ConfirmationPending, VoteState.ConfirmationReceived),
            (VoteState.ConfirmationReceived, VoteState.VotePersisted),
            (VoteState.VotePersisted, VoteState.UserNotified),
            (VoteState.UserNotified, VoteState.CooldownActive),
            (VoteState.CooldownActive, VoteState.Idle)
        };

        foreach (var (current, next) in transitions)
        {
            Assert.True(VoteStateMachine.CanTransition(current, next));
        }
    }

    [Fact]
    public void InvalidTransition_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => VoteStateMachine.Transition(VoteState.Idle, VoteState.CooldownActive));
    }
}

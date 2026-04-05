using GameController.FBServiceExt.Application.Abstractions.Persistence;
using GameController.FBServiceExt.Application.Contracts.Votes;
using GameController.FBServiceExt.Infrastructure.Data;
using GameController.FBServiceExt.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GameController.FBServiceExt.Infrastructure.Persistence;

internal sealed class SqlAcceptedVoteStore : IAcceptedVoteStore
{
    private readonly IDbContextFactory<FbServiceExtDbContext> _dbContextFactory;

    public SqlAcceptedVoteStore(IDbContextFactory<FbServiceExtDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async ValueTask<bool> TryAddAsync(AcceptedVote vote, CancellationToken cancellationToken)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        dbContext.AcceptedVotes.Add(new AcceptedVoteEntity
        {
            VoteId = vote.VoteId,
            CorrelationId = vote.CorrelationId,
            UserId = vote.UserId,
            RecipientId = vote.RecipientId,
            ShowId = vote.ShowId,
            CandidateId = vote.CandidateId,
            CandidateDisplayName = vote.CandidateDisplayName,
            SourceEventId = vote.SourceEventId,
            ConfirmedAtUtc = vote.ConfirmedAtUtc,
            CooldownUntilUtc = vote.CooldownUntilUtc,
            Channel = vote.Channel,
            MetadataJson = vote.MetadataJson,
            UserAccountName = vote.UserAccountName
        });

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex.IsUniqueConstraintViolation())
        {
            return false;
        }
    }
}

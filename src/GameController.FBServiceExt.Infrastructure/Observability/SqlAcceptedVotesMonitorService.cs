using GameController.FBServiceExt.Application.Abstractions.Observability;
using GameController.FBServiceExt.Application.Contracts.Observability;
using GameController.FBServiceExt.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace GameController.FBServiceExt.Infrastructure.Observability;

internal sealed class SqlAcceptedVotesMonitorService : IAcceptedVotesMonitorService
{
    private readonly IDbContextFactory<FbServiceExtDbContext> _dbContextFactory;

    public SqlAcceptedVotesMonitorService(IDbContextFactory<FbServiceExtDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async ValueTask<AcceptedVotesMonitorSnapshot> GetSnapshotAsync(DateTime? fromUtc, DateTime? toUtc, string? showId, int recentLimit, CancellationToken cancellationToken)
    {
        var safeLimit = Math.Clamp(recentLimit, 20, 500);
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var query = dbContext.AcceptedVotes.AsNoTracking().AsQueryable();
        if (fromUtc.HasValue)
        {
            query = query.Where(v => v.ConfirmedAtUtc >= fromUtc.Value);
        }
        if (toUtc.HasValue)
        {
            query = query.Where(v => v.ConfirmedAtUtc <= toUtc.Value);
        }
        if (!string.IsNullOrWhiteSpace(showId))
        {
            query = query.Where(v => v.ShowId == showId);
        }

        var totalVotes = await query.CountAsync(cancellationToken);
        var totalUniqueUsers = await query
            .Select(v => new { v.RecipientId, v.UserId })
            .Distinct()
            .CountAsync(cancellationToken);

        var candidateRows = await query
            .GroupBy(v => new { v.CandidateId, v.CandidateDisplayName })
            .Select(g => new
            {
                g.Key.CandidateId,
                g.Key.CandidateDisplayName,
                VoteCount = g.Count(),
                UniqueUsers = g.Select(v => new { v.RecipientId, v.UserId }).Distinct().Count()
            })
            .OrderByDescending(x => x.VoteCount)
            .ThenBy(x => x.CandidateDisplayName)
            .ToListAsync(cancellationToken);

        var topFanRows = await query
            .GroupBy(v => new { v.CandidateId, v.CandidateDisplayName, v.UserId, v.UserAccountName })
            .Select(g => new
            {
                g.Key.CandidateId,
                g.Key.CandidateDisplayName,
                g.Key.UserId,
                g.Key.UserAccountName,
                VoteCount = g.Count()
            })
            .OrderByDescending(x => x.VoteCount)
            .ThenBy(x => x.UserAccountName)
            .ToListAsync(cancellationToken);

        var topFansByCandidate = topFanRows
            .GroupBy(x => (x.CandidateId, x.CandidateDisplayName))
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<AcceptedVotesMonitorTopFan>)g
                    .Take(3)
                    .Select(x => new AcceptedVotesMonitorTopFan(
                        x.UserId,
                        string.IsNullOrWhiteSpace(x.UserAccountName) ? x.UserId : x.UserAccountName!,
                        x.VoteCount))
                    .ToList());

        var candidates = candidateRows
            .Select(row => new AcceptedVotesMonitorCandidateSummary(
                row.CandidateId,
                row.CandidateDisplayName,
                row.VoteCount,
                totalVotes == 0 ? 0d : (double)row.VoteCount / totalVotes * 100d,
                row.UniqueUsers,
                topFansByCandidate.TryGetValue((row.CandidateId, row.CandidateDisplayName), out var topFans)
                    ? topFans
                    : Array.Empty<AcceptedVotesMonitorTopFan>()))
            .ToList();

        var recentVotes = await query
            .OrderByDescending(v => v.ConfirmedAtUtc)
            .ThenByDescending(v => v.RecordedAtUtc)
            .Take(safeLimit)
            .Select(v => new AcceptedVotesMonitorRecentVote(
                v.UserId,
                string.IsNullOrWhiteSpace(v.UserAccountName) ? v.UserId : v.UserAccountName!,
                v.CandidateId,
                v.CandidateDisplayName,
                v.ShowId,
                v.ConfirmedAtUtc,
                v.RecordedAtUtc))
            .ToListAsync(cancellationToken);

        return new AcceptedVotesMonitorSnapshot(
            DateTime.UtcNow,
            fromUtc,
            toUtc,
            string.IsNullOrWhiteSpace(showId) ? null : showId,
            totalVotes,
            totalUniqueUsers,
            candidates,
            recentVotes);
    }
}

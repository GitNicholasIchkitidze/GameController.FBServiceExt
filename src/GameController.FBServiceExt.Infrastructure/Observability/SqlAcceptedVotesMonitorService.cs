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

    // Poll Monitor/UI-სთვის AcceptedVotes-იდან აგენერირებს aggregate snapshot-ს, top fans-ს და recent votes page-ს.
    public async ValueTask<AcceptedVotesMonitorSnapshot> GetSnapshotAsync(
        DateTime? fromUtc,
        DateTime? toUtc,
        string? showId,
        string? userFilter,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var safePage = Math.Max(1, page);
        var safePageSize = Math.Clamp(pageSize, 25, 500);
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
            .GroupBy(v => new { v.RecipientId, v.UserId })
            .Select(g => 1)
            .CountAsync(cancellationToken);

        var candidateRows = await query
            .GroupBy(v => new { v.CandidateId, v.CandidateDisplayName })
            .Select(g => new
            {
                g.Key.CandidateId,
                g.Key.CandidateDisplayName,
                VoteCount = g.Count(),
                UniqueUsers = g
                    .GroupBy(v => new { v.RecipientId, v.UserId })
                    .Select(userGroup => 1)
                    .Count()
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

        var recentQuery = query;
        if (!string.IsNullOrWhiteSpace(userFilter))
        {
            var normalizedUserFilter = userFilter.Trim();
            recentQuery = recentQuery.Where(v =>
                v.UserId.Contains(normalizedUserFilter) ||
                (v.UserAccountName != null && v.UserAccountName.Contains(normalizedUserFilter)) ||
                v.CandidateDisplayName.Contains(normalizedUserFilter) ||
                v.CandidateId.Contains(normalizedUserFilter));
        }

        var recentTotalCount = await recentQuery.CountAsync(cancellationToken);
        var recentTotalPages = Math.Max(1, (int)Math.Ceiling(recentTotalCount / (double)safePageSize));
        if (safePage > recentTotalPages)
        {
            safePage = recentTotalPages;
        }

        var recentItems = await recentQuery
            .OrderByDescending(v => v.ConfirmedAtUtc)
            .ThenByDescending(v => v.RecordedAtUtc)
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
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
            new AcceptedVotesMonitorRecentVotesPage(
                safePage,
                safePageSize,
                recentTotalCount,
                recentTotalPages,
                recentItems));
    }
}

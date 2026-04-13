using GameController.FBServiceExt.Application.Abstractions.Persistence;
using GameController.FBServiceExt.Application.Contracts.Normalization;
using GameController.FBServiceExt.Infrastructure.Data;
using GameController.FBServiceExt.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GameController.FBServiceExt.Infrastructure.Persistence;

internal sealed class SqlNormalizedEventStore : INormalizedEventStore
{
    private readonly IDbContextFactory<FbServiceExtDbContext> _dbContextFactory;

    public SqlNormalizedEventStore(IDbContextFactory<FbServiceExtDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    // საჭიროების შემთხვევაში normalized event-ს audit/debug მიზნებისთვის NormalizedEvents ცხრილში წერს.
    public async ValueTask<bool> TryAddAsync(NormalizedMessengerEvent normalizedEvent, CancellationToken cancellationToken)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        dbContext.NormalizedEvents.Add(new NormalizedEventEntity
        {
            EventId = normalizedEvent.EventId,
            RawEnvelopeId = normalizedEvent.RawEnvelopeId,
            EventType = normalizedEvent.EventType,
            MessageId = normalizedEvent.MessageId,
            SenderId = normalizedEvent.SenderId,
            RecipientId = normalizedEvent.RecipientId,
            OccurredAtUtc = normalizedEvent.OccurredAtUtc,
            PayloadJson = normalizedEvent.PayloadJson
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

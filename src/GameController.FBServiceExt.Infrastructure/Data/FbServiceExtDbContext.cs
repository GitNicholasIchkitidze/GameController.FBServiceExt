using GameController.FBServiceExt.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GameController.FBServiceExt.Infrastructure.Data;

internal sealed class FbServiceExtDbContext : DbContext
{
    public FbServiceExtDbContext(DbContextOptions<FbServiceExtDbContext> options)
        : base(options)
    {
    }

    public DbSet<NormalizedEventEntity> NormalizedEvents => Set<NormalizedEventEntity>();

    public DbSet<AcceptedVoteEntity> AcceptedVotes => Set<AcceptedVoteEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NormalizedEventEntity>(entity =>
        {
            entity.ToTable("NormalizedEvents");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedOnAdd();
            entity.Property(item => item.EventId).HasMaxLength(200).IsRequired();
            entity.Property(item => item.MessageId).HasMaxLength(200);
            entity.Property(item => item.SenderId).HasMaxLength(200);
            entity.Property(item => item.RecipientId).HasMaxLength(200);
            entity.Property(item => item.PayloadJson).IsRequired();
            entity.Property(item => item.EventType).HasConversion<int>().IsRequired();
            entity.Property(item => item.RecordedAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");
            entity.HasIndex(item => item.EventId).IsUnique();
            entity.HasIndex(item => new { item.SenderId, item.RecipientId, item.OccurredAtUtc });
        });

        modelBuilder.Entity<AcceptedVoteEntity>(entity =>
        {
            entity.ToTable("AcceptedVotes");
            entity.HasKey(item => item.VoteId);
            entity.Property(item => item.CorrelationId).HasMaxLength(200).IsRequired();
            entity.Property(item => item.UserId).HasMaxLength(200).IsRequired();
            entity.Property(item => item.RecipientId).HasMaxLength(200).IsRequired();
            entity.Property(item => item.ShowId).HasMaxLength(200).IsRequired();
            entity.Property(item => item.CandidateId).HasMaxLength(200).IsRequired();
            entity.Property(item => item.CandidateDisplayName).HasMaxLength(400).IsRequired();
            entity.Property(item => item.SourceEventId).HasMaxLength(200).IsRequired();
            entity.Property(item => item.Channel).HasMaxLength(50).IsRequired();
            entity.Property(item => item.UserAccountName).HasMaxLength(400);
            entity.Property(item => item.RecordedAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");
            entity.HasIndex(item => item.SourceEventId).IsUnique();
            entity.HasIndex(item => new { item.UserId, item.ConfirmedAtUtc });
            entity.HasIndex(item => new { item.ShowId, item.ConfirmedAtUtc });
        });

        base.OnModelCreating(modelBuilder);
    }
}

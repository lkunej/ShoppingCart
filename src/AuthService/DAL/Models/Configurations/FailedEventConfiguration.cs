using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shared.Models.Entities;

namespace AuthService.DAL.Models.Configurations;

public class FailedEventConfiguration : IEntityTypeConfiguration<FailedEvent>
{
    public void Configure(EntityTypeBuilder<FailedEvent> builder)
    {
        builder.ToTable("failed_events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.EventType).HasMaxLength(100).IsRequired();
        builder.Property(e => e.RoutingKey).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Payload).IsRequired();
        builder.Property(e => e.CorrelationId).HasMaxLength(100);
        builder.Property(e => e.RetryCount).IsRequired();
        builder.Property(e => e.LastError).HasMaxLength(1000);
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("NOW()");
        builder.Property(e => e.ProcessedAt);

        builder.HasIndex(e => e.ProcessedAt)
            .HasFilter("\"ProcessedAt\" IS NULL")
            .HasDatabaseName("IX_failed_events_unprocessed");
    }
}

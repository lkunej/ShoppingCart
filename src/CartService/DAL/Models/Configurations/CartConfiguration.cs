using CartService.DAL.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CartService.DAL.Models.Configurations;

public class CartConfiguration : IEntityTypeConfiguration<Cart>
{
    public void Configure(EntityTypeBuilder<Cart> builder)
    {
        builder.ToTable("carts");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(c => c.UserId).IsRequired();
        // Unique index only for authenticated carts (guest carts all use Guid.Empty)
        builder.HasIndex(c => c.UserId)
               .IsUnique()
               .HasFilter("\"GuestSessionToken\" IS NULL");
        builder.Property(c => c.CreatedAt).HasDefaultValueSql("NOW()");
        builder.Property(c => c.UpdatedAt).HasDefaultValueSql("NOW()");
        builder.HasMany(c => c.Items)
               .WithOne(i => i.Cart)
               .HasForeignKey(i => i.CartId)
               .OnDelete(DeleteBehavior.Cascade);

        // Guest session token - nullable for authenticated carts
        builder.Property(c => c.GuestSessionToken).IsRequired(false);

        // Unique filtered index: one cart per guest session token
        builder.HasIndex(c => c.GuestSessionToken)
               .IsUnique()
               .HasDatabaseName("IX_Carts_GuestSessionToken")
               .HasFilter("\"GuestSessionToken\" IS NOT NULL");

        // Filtered index on UpdatedAt for efficient expired guest cart lookups
        builder.HasIndex(c => c.UpdatedAt)
               .HasDatabaseName("IX_Carts_GuestSessionToken_UpdatedAt")
               .HasFilter("\"GuestSessionToken\" IS NOT NULL");
    }
}

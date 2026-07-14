using CartService.DAL.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CartService.DAL.Models.Configurations;

public class CartItemConfiguration : IEntityTypeConfiguration<CartItem>
{
    public void Configure(EntityTypeBuilder<CartItem> builder)
    {
        builder.ToTable("cart_items");
        builder.HasKey(ci => ci.Id);
        builder.Property(ci => ci.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(ci => ci.ProductId).IsRequired();
        builder.Property(ci => ci.ProductName).HasMaxLength(255).IsRequired();
        builder.Property(ci => ci.UnitPriceAmount).IsRequired();
        builder.Property(ci => ci.UnitPriceCurrency).HasMaxLength(3).HasDefaultValue("EUR");
        builder.Property(ci => ci.Quantity).IsRequired();
        builder.HasIndex(ci => new { ci.CartId, ci.ProductId }).IsUnique();
        builder.Property(ci => ci.CreatedAt).HasDefaultValueSql("NOW()");
        builder.Property(ci => ci.UpdatedAt).HasDefaultValueSql("NOW()");
    }
}

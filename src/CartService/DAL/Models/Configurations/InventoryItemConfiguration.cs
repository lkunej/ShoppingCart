using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CartService.DAL.Models.Configurations;

public class InventoryItemConfiguration : IEntityTypeConfiguration<InventoryItem>
{
    public void Configure(EntityTypeBuilder<InventoryItem> builder)
    {
        builder.ToTable("inventory_items");
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(i => i.ProductId).IsRequired();
        builder.HasIndex(i => i.ProductId).IsUnique();
        builder.Property(i => i.ProductName).HasMaxLength(255).IsRequired();
        builder.Property(i => i.AvailableQuantity).IsRequired();
        builder.Property(i => i.UnitPriceAmount).IsRequired();
        builder.Property(i => i.UnitPriceCurrency).HasMaxLength(3).HasDefaultValue("EUR");
        builder.Property(i => i.UpdatedAt).HasDefaultValueSql("NOW()");

        // Seed data — sample products for the PoC demo
        builder.HasData(
            new InventoryItem
            {
                Id = Guid.Parse("a1b2c3d4-0001-0001-0001-000000000001"),
                ProductId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                ProductName = "Wireless Bluetooth Headphones",
                AvailableQuantity = 150,
                UnitPriceAmount = 7999, // €79.99
                UnitPriceCurrency = "EUR",
                UpdatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            },
            new InventoryItem
            {
                Id = Guid.Parse("a1b2c3d4-0001-0001-0001-000000000002"),
                ProductId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                ProductName = "USB-C Charging Cable (2m)",
                AvailableQuantity = 500,
                UnitPriceAmount = 1299, // €12.99
                UnitPriceCurrency = "EUR",
                UpdatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            },
            new InventoryItem
            {
                Id = Guid.Parse("a1b2c3d4-0001-0001-0001-000000000003"),
                ProductId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                ProductName = "Mechanical Keyboard (TKL)",
                AvailableQuantity = 75,
                UnitPriceAmount = 12999, // €129.99
                UnitPriceCurrency = "EUR",
                UpdatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            },
            new InventoryItem
            {
                Id = Guid.Parse("a1b2c3d4-0001-0001-0001-000000000004"),
                ProductId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                ProductName = "Ergonomic Mouse",
                AvailableQuantity = 200,
                UnitPriceAmount = 4999, // €49.99
                UnitPriceCurrency = "EUR",
                UpdatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            },
            new InventoryItem
            {
                Id = Guid.Parse("a1b2c3d4-0001-0001-0001-000000000005"),
                ProductId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
                ProductName = "27\" 4K Monitor",
                AvailableQuantity = 30,
                UnitPriceAmount = 44999, // €449.99
                UnitPriceCurrency = "EUR",
                UpdatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            },
            new InventoryItem
            {
                Id = Guid.Parse("a1b2c3d4-0001-0001-0001-000000000006"),
                ProductId = Guid.Parse("66666666-6666-6666-6666-666666666666"),
                ProductName = "Laptop Stand (Aluminum)",
                AvailableQuantity = 120,
                UnitPriceAmount = 3499, // €34.99
                UnitPriceCurrency = "EUR",
                UpdatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            },
            new InventoryItem
            {
                Id = Guid.Parse("a1b2c3d4-0001-0001-0001-000000000007"),
                ProductId = Guid.Parse("77777777-7777-7777-7777-777777777777"),
                ProductName = "Webcam 1080p",
                AvailableQuantity = 90,
                UnitPriceAmount = 5999, // €59.99
                UnitPriceCurrency = "EUR",
                UpdatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            },
            new InventoryItem
            {
                Id = Guid.Parse("a1b2c3d4-0001-0001-0001-000000000008"),
                ProductId = Guid.Parse("88888888-8888-8888-8888-888888888888"),
                ProductName = "Desk Lamp (LED, Dimmable)",
                AvailableQuantity = 250,
                UnitPriceAmount = 2499, // €24.99
                UnitPriceCurrency = "EUR",
                UpdatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            },
            new InventoryItem
            {
                Id = Guid.Parse("a1b2c3d4-0001-0001-0001-000000000009"),
                ProductId = Guid.Parse("99999999-9999-9999-9999-999999999999"),
                ProductName = "Noise Cancelling Earbuds",
                AvailableQuantity = 5, // Low stock — good for testing insufficient stock
                UnitPriceAmount = 19999, // €199.99
                UnitPriceCurrency = "EUR",
                UpdatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            },
            new InventoryItem
            {
                Id = Guid.Parse("a1b2c3d4-0001-0001-0001-000000000010"),
                ProductId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                ProductName = "Portable SSD (1TB)",
                AvailableQuantity = 60,
                UnitPriceAmount = 8999, // €89.99
                UnitPriceCurrency = "EUR",
                UpdatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            }
        );
    }
}

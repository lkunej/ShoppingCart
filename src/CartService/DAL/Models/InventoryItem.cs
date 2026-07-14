namespace CartService.DAL.Models;

/// <summary>
/// Represents a product's inventory record.
/// In a production system this would live in a separate Inventory Service;
/// for this PoC it's co-located in the Cart database for simplicity.
/// </summary>
public class InventoryItem
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public int AvailableQuantity { get; set; }
    public int UnitPriceAmount { get; set; } // cents
    public string UnitPriceCurrency { get; set; } = "EUR";
    public DateTime UpdatedAt { get; set; }
}

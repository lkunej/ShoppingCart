namespace CartService.DAL.Models;

public class CartItem
{
    public Guid Id { get; set; }
    public Guid CartId { get; set; }    
    public Guid ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public int UnitPriceAmount { get; set; }       // cents
    public string UnitPriceCurrency { get; set; } = "EUR";
    public int Quantity { get; set; }              // 1-9999
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public virtual Cart Cart { get; set; } = null!;
}

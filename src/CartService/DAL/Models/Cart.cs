namespace CartService.DAL.Models;

public class Cart
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public virtual ICollection<CartItem> Items { get; set; } = new List<CartItem>();
}

namespace Shared.Models.Exceptions;

/// <summary>
/// Thrown when the requested quantity exceeds available inventory for a product.
/// </summary>
public class InsufficientStockException : Exception
{
    public Guid ProductId { get; }
    public int RequestedQuantity { get; }
    public int AvailableQuantity { get; }

    public InsufficientStockException(Guid productId, int requestedQuantity, int availableQuantity)
        : base($"Insufficient stock for product {productId}: requested {requestedQuantity}, available {availableQuantity}.")
    {
        ProductId = productId;
        RequestedQuantity = requestedQuantity;
        AvailableQuantity = availableQuantity;
    }
}

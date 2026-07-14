namespace Shared.Models.Interfaces;

/// <summary>
/// Result from checking product availability, including product metadata.
/// </summary>
public record InventoryCheckResult(
    Guid ProductId,
    string ProductName,
    int AvailableQuantity,
    int UnitPriceAmount,
    string UnitPriceCurrency
);

public interface IInventoryClient
{
    /// <summary>
    /// Checks the availability of a product from the Inventory Service.
    /// Returns the available quantity for the specified product.
    /// Throws InsufficientStockException if available quantity is less than requestedQuantity.
    /// Throws ServiceUnavailableException if the circuit breaker is open.
    /// Uses circuit breaker to prevent cascading failures.
    /// </summary>
    Task<int> CheckAvailability(Guid productId, int requestedQuantity);

    /// <summary>
    /// Checks availability and returns full product info (name, price, stock) in a single query.
    /// Throws InsufficientStockException if available quantity is less than requestedQuantity.
    /// Throws KeyNotFoundException if the product doesn't exist.
    /// </summary>
    Task<InventoryCheckResult> CheckAvailabilityWithProductInfo(Guid productId, int requestedQuantity);
}

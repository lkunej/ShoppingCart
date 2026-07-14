using CartService.DAL.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shared.Models.Exceptions;
using Shared.Models.Interfaces;

namespace CartService.Infrastructure;

/// <summary>
/// Database-backed inventory client that queries a local inventory table.
/// Keeps the same IInventoryClient contract so the rest of the Cart Service is unchanged.
/// 
/// In a full production system this would be extracted into a separate Inventory Service
/// accessed via HTTP/gRPC with circuit breaker resilience. For this PoC the inventory
/// data is co-located in the Cart database for simplicity.
/// </summary>
public class InventoryClient : IInventoryClient
{
    private readonly CartDbContext _dbContext;
    private readonly ILogger<InventoryClient> _logger;

    public InventoryClient(CartDbContext dbContext, ILogger<InventoryClient> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <summary>
    /// Checks product availability from the local inventory table.
    /// Returns the available quantity for the specified product.
    /// Throws InsufficientStockException if available quantity is less than requestedQuantity.
    /// Throws KeyNotFoundException if the product doesn't exist.
    /// </summary>
    public async Task<int> CheckAvailability(Guid productId, int requestedQuantity)
    {
        var result = await CheckAvailabilityWithProductInfo(productId, requestedQuantity);
        return result.AvailableQuantity;
    }

    /// <summary>
    /// Checks availability and returns full product info (name, price, stock) in a single query.
    /// Eliminates the need for a separate product info lookup.
    /// </summary>
    public async Task<InventoryCheckResult> CheckAvailabilityWithProductInfo(Guid productId, int requestedQuantity)
    {
        var inventoryItem = await _dbContext.InventoryItems
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.ProductId == productId);

        if (inventoryItem is null)
        {
            _logger.LogWarning("Product {ProductId} not found in inventory.", productId);
            throw new KeyNotFoundException($"Product {productId} not found in inventory.");
        }

        if (inventoryItem.AvailableQuantity < requestedQuantity)
        {
            _logger.LogInformation(
                "Insufficient stock for product {ProductId}: requested {Requested}, available {Available}.",
                productId, requestedQuantity, inventoryItem.AvailableQuantity);

            throw new InsufficientStockException(productId, requestedQuantity, inventoryItem.AvailableQuantity);
        }

        return new InventoryCheckResult(
            inventoryItem.ProductId,
            inventoryItem.ProductName,
            inventoryItem.AvailableQuantity,
            inventoryItem.UnitPriceAmount,
            inventoryItem.UnitPriceCurrency
        );
    }
}

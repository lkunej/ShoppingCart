using CartService.DAL.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shared.Middleware;

namespace CartService.Controllers;

/// <summary>
/// Admin-only endpoints for browsing and managing inventory items.
/// Requires the "admin:read" permission (Admin role only).
/// </summary>
[ApiController]
[Route("api/inventory")]
[Authorize]
[RequirePermission("admin:read")]
public class InventoryController : ControllerBase
{
    private readonly CartDbContext _db;

    public InventoryController(CartDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// GET /api/inventory — List all available inventory items.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var items = await _db.InventoryItems
            .OrderBy(i => i.ProductName)
            .Select(i => new
            {
                i.Id,
                i.ProductId,
                i.ProductName,
                i.AvailableQuantity,
                PricePerUnit = $"{i.UnitPriceAmount / 100m:F2} {i.UnitPriceCurrency}",
                i.UnitPriceAmount,
                i.UnitPriceCurrency,
                i.UpdatedAt
            })
            .ToListAsync();

        return Ok(items);
    }

    /// <summary>
    /// GET /api/inventory/{productId} — Get a specific inventory item by product ID.
    /// </summary>
    [HttpGet("{productId:guid}")]
    public async Task<IActionResult> GetByProductId(Guid productId)
    {
        var item = await _db.InventoryItems
            .Where(i => i.ProductId == productId)
            .Select(i => new
            {
                i.Id,
                i.ProductId,
                i.ProductName,
                i.AvailableQuantity,
                PricePerUnit = $"{i.UnitPriceAmount / 100m:F2} {i.UnitPriceCurrency}",
                i.UnitPriceAmount,
                i.UnitPriceCurrency,
                i.UpdatedAt
            })
            .FirstOrDefaultAsync();

        if (item == null)
            return NotFound(new { error = "NotFound", message = $"No inventory item found for product {productId}." });

        return Ok(item);
    }
}

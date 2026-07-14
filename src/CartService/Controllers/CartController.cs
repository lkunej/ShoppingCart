using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.Middleware;
using Shared.Models.DTOs;
using Shared.Models.Exceptions;
using Shared.Models.Interfaces;

namespace CartService.Controllers;

[ApiController]
[Route("api/cart")]
[Authorize]
public class CartController : ControllerBase
{
    private readonly ICartService _cartService;
    private readonly ILogger<CartController> _logger;

    public CartController(ICartService cartService, ILogger<CartController> logger)
    {
        _cartService = cartService;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/cart — Returns the user's cart.
    /// </summary>
    [HttpGet]
    [RequirePermission("cart:read")]
    public async Task<IActionResult> GetCart()
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized(new ErrorResponse("Unauthorized", "Missing or invalid X-User-Id header."));
        }

        try
        {
            var cart = await _cartService.GetCart(userId);
            return Ok(cart);
        }
        catch (ServiceUnavailableException ex)
        {
            _logger.LogWarning(ex, "Service unavailable while getting cart for user {UserId}.", userId);
            return StatusCode(503, new ErrorResponse("ServiceUnavailable", ex.Message));
        }
    }

    /// <summary>
    /// POST /api/cart/items — Add an item to the cart.
    /// </summary>
    [HttpPost("items")]
    [RequirePermission("cart:write")]
    public async Task<IActionResult> AddItem([FromBody] AddItemRequest request)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized(new ErrorResponse("Unauthorized", "Missing or invalid X-User-Id header."));
        }

        if (request.Quantity < 1 || request.Quantity > 9999)
        {
            return BadRequest(new ErrorResponse("ValidationError", "Quantity must be between 1 and 9999."));
        }

        try
        {
            var cart = await _cartService.AddItem(userId, request.ProductId, request.Quantity);
            return Ok(cart);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new ErrorResponse("NotFound", ex.Message));
        }
        catch (InsufficientStockException ex)
        {
            return Conflict(new ErrorResponse("InsufficientStock", ex.Message,
                $"Available quantity: {ex.AvailableQuantity}"));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ErrorResponse("ValidationError", ex.Message));
        }
        catch (ServiceUnavailableException ex)
        {
            _logger.LogWarning(ex, "Service unavailable while adding item for user {UserId}.", userId);
            return StatusCode(503, new ErrorResponse("ServiceUnavailable", ex.Message));
        }
    }

    /// <summary>
    /// PUT /api/cart/items/{itemId} — Update item quantity.
    /// </summary>
    [HttpPut("items/{itemId:guid}")]
    [RequirePermission("cart:write")]
    public async Task<IActionResult> UpdateItem(Guid itemId, [FromBody] UpdateItemRequest request)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized(new ErrorResponse("Unauthorized", "Missing or invalid X-User-Id header."));
        }

        if (request.Quantity < 1 || request.Quantity > 999)
        {
            return BadRequest(new ErrorResponse("ValidationError", "Quantity must be between 1 and 999."));
        }

        try
        {
            var cart = await _cartService.UpdateItem(userId, itemId, request.Quantity);
            return Ok(cart);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new ErrorResponse("NotFound", ex.Message));
        }
        catch (InsufficientStockException ex)
        {
            return Conflict(new ErrorResponse("InsufficientStock", ex.Message,
                $"Available quantity: {ex.AvailableQuantity}"));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ErrorResponse("ValidationError", ex.Message));
        }
        catch (ServiceUnavailableException ex)
        {
            _logger.LogWarning(ex, "Service unavailable while updating item for user {UserId}.", userId);
            return StatusCode(503, new ErrorResponse("ServiceUnavailable", ex.Message));
        }
    }

    /// <summary>
    /// DELETE /api/cart/items/{itemId} — Remove an item from the cart.
    /// </summary>
    [HttpDelete("items/{itemId:guid}")]
    [RequirePermission("cart:write")]
    public async Task<IActionResult> RemoveItem(Guid itemId)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized(new ErrorResponse("Unauthorized", "Missing or invalid X-User-Id header."));
        }

        try
        {
            var cart = await _cartService.RemoveItem(userId, itemId);
            return Ok(cart);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new ErrorResponse("NotFound", ex.Message));
        }
        catch (ServiceUnavailableException ex)
        {
            _logger.LogWarning(ex, "Service unavailable while removing item for user {UserId}.", userId);
            return StatusCode(503, new ErrorResponse("ServiceUnavailable", ex.Message));
        }
    }

    /// <summary>
    /// Extracts userId from the X-User-Id header forwarded by the API Gateway.
    /// </summary>
    private bool TryGetUserId(out Guid userId)
    {
        userId = Guid.Empty;

        if (!Request.Headers.TryGetValue("X-User-Id", out var headerValue))
        {
            return false;
        }

        var value = headerValue.ToString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return Guid.TryParse(value, out userId);
    }
}

/// <summary>
/// Request body for POST /api/cart/items.
/// </summary>
public record AddItemRequest(Guid ProductId, int Quantity);

/// <summary>
/// Request body for PUT /api/cart/items/{itemId}.
/// </summary>
public record UpdateItemRequest(int Quantity);

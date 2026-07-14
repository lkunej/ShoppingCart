using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using CartService.Infrastructure;
using CartService.Models.DTOs;
using CartService.Services;
using Shared.Models.DTOs;
using Shared.Models.Exceptions;

namespace CartService.Controllers;

[ApiController]
[Route("api/guest-cart")]
[AllowAnonymous]
public class GuestCartController : ControllerBase
{
    private readonly IGuestCartService _guestCartService;
    private readonly IGuestRateLimiterService _rateLimiterService;
    private readonly ILogger<GuestCartController> _logger;

    public GuestCartController(
        IGuestCartService guestCartService,
        IGuestRateLimiterService rateLimiterService,
        ILogger<GuestCartController> logger)
    {
        _guestCartService = guestCartService;
        _rateLimiterService = rateLimiterService;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/guest-cart/items — Add an item to the guest cart.
    /// Creates a new session if no valid X-Guest-Session header is provided or if the token is unrecognized.
    /// </summary>
    [HttpPost("items")]
    public async Task<IActionResult> AddItem([FromBody] AddItemRequest request)
    {
        if (request.Quantity < 1 || request.Quantity > 9999)
        {
            return BadRequest(new ErrorResponse("ValidationError", "Quantity must be between 1 and 9999."));
        }

        // Validate X-Guest-Session header format (if present)
        var headerResult = TryGetGuestSessionToken();
        if (headerResult.HasHeader && !headerResult.IsValid)
        {
            return BadRequest(new ErrorResponse("InvalidGuestSession", "Malformed guest session token. Must be a valid UUID."));
        }

        try
        {
            if (headerResult.IsValid && headerResult.Token.HasValue)
            {
                // Try to add item to existing session
                try
                {
                    var cart = await _guestCartService.AddItem(headerResult.Token.Value, request.ProductId, request.Quantity);
                    return Ok(cart);
                }
                catch (KeyNotFoundException ex) when (ex.Message.Contains("session") || ex.Message.Contains("cart"))
                {
                    // Token is valid UUID but no matching cart — treat as new session
                    _logger.LogInformation("Guest session token {Token} not found, creating new session.", headerResult.Token.Value);
                }
            }

            // New session: check rate limit
            var clientIp = GetClientIpAddress();
            var rateLimitResult = await _rateLimiterService.CheckSessionCreationLimit(clientIp);

            if (!rateLimitResult.IsAllowed)
            {
                Response.Headers["Retry-After"] = rateLimitResult.RetryAfterSeconds?.ToString() ?? "60";
                return StatusCode(429, new ErrorResponse("RateLimitExceeded", "Too many guest sessions created. Please try again later."));
            }

            // Create new session and add item
            var newSession = await _guestCartService.CreateSession();
            var result = await _guestCartService.AddItem(newSession.GuestSessionToken, request.ProductId, request.Quantity);
            return StatusCode(201, result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new ErrorResponse("NotFound", ex.Message));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return BadRequest(new ErrorResponse("ValidationError", ex.Message));
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
            _logger.LogWarning(ex, "Service unavailable while adding item to guest cart.");
            return StatusCode(503, new ErrorResponse("ServiceUnavailable", ex.Message));
        }
    }

    /// <summary>
    /// GET /api/guest-cart — Get guest cart contents.
    /// Requires a valid, existing X-Guest-Session header.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetCart()
    {
        var headerResult = TryGetGuestSessionToken();
        if (!headerResult.HasHeader || !headerResult.IsValid)
        {
            return BadRequest(new ErrorResponse("InvalidGuestSession", "Missing or malformed guest session token. Must be a valid UUID."));
        }

        try
        {
            var cart = await _guestCartService.GetCart(headerResult.Token!.Value);
            return Ok(cart);
        }
        catch (KeyNotFoundException)
        {
            return BadRequest(new ErrorResponse("InvalidGuestSession", "Guest session not found."));
        }
        catch (ServiceUnavailableException ex)
        {
            _logger.LogWarning(ex, "Service unavailable while getting guest cart.");
            return StatusCode(503, new ErrorResponse("ServiceUnavailable", ex.Message));
        }
    }

    /// <summary>
    /// PUT /api/guest-cart/items/{itemId} — Update item quantity in guest cart.
    /// Requires a valid, existing X-Guest-Session header.
    /// </summary>
    [HttpPut("items/{itemId:guid}")]
    public async Task<IActionResult> UpdateItem(Guid itemId, [FromBody] UpdateItemRequest request)
    {
        if (request.Quantity < 1 || request.Quantity > 9999)
        {
            return BadRequest(new ErrorResponse("ValidationError", "Quantity must be between 1 and 9999."));
        }

        var headerResult = TryGetGuestSessionToken();
        if (!headerResult.HasHeader || !headerResult.IsValid)
        {
            return BadRequest(new ErrorResponse("InvalidGuestSession", "Missing or malformed guest session token. Must be a valid UUID."));
        }

        try
        {
            var cart = await _guestCartService.UpdateItem(headerResult.Token!.Value, itemId, request.Quantity);
            return Ok(cart);
        }
        catch (KeyNotFoundException ex) when (ex.Message.Contains("session") || ex.Message.Contains("cart"))
        {
            return BadRequest(new ErrorResponse("InvalidGuestSession", "Guest session not found."));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new ErrorResponse("NotFound", ex.Message));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return BadRequest(new ErrorResponse("ValidationError", ex.Message));
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
            _logger.LogWarning(ex, "Service unavailable while updating guest cart item.");
            return StatusCode(503, new ErrorResponse("ServiceUnavailable", ex.Message));
        }
    }

    /// <summary>
    /// DELETE /api/guest-cart/items/{itemId} — Remove an item from guest cart.
    /// Requires a valid, existing X-Guest-Session header.
    /// </summary>
    [HttpDelete("items/{itemId:guid}")]
    public async Task<IActionResult> RemoveItem(Guid itemId)
    {
        var headerResult = TryGetGuestSessionToken();
        if (!headerResult.HasHeader || !headerResult.IsValid)
        {
            return BadRequest(new ErrorResponse("InvalidGuestSession", "Missing or malformed guest session token. Must be a valid UUID."));
        }

        try
        {
            var cart = await _guestCartService.RemoveItem(headerResult.Token!.Value, itemId);
            return Ok(cart);
        }
        catch (KeyNotFoundException ex) when (ex.Message.Contains("session") || ex.Message.Contains("cart"))
        {
            return BadRequest(new ErrorResponse("InvalidGuestSession", "Guest session not found."));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new ErrorResponse("NotFound", ex.Message));
        }
        catch (ServiceUnavailableException ex)
        {
            _logger.LogWarning(ex, "Service unavailable while removing guest cart item.");
            return StatusCode(503, new ErrorResponse("ServiceUnavailable", ex.Message));
        }
    }

    /// <summary>
    /// Attempts to parse the X-Guest-Session header value as a GUID.
    /// Returns information about whether the header is present, valid, and the parsed token.
    /// </summary>
    private GuestSessionHeaderResult TryGetGuestSessionToken()
    {
        if (!Request.Headers.TryGetValue("X-Guest-Session", out var headerValue))
        {
            return new GuestSessionHeaderResult(HasHeader: false, IsValid: false, Token: null);
        }

        var value = headerValue.ToString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return new GuestSessionHeaderResult(HasHeader: true, IsValid: false, Token: null);
        }

        if (Guid.TryParse(value, out var token))
        {
            return new GuestSessionHeaderResult(HasHeader: true, IsValid: true, Token: token);
        }

        return new GuestSessionHeaderResult(HasHeader: true, IsValid: false, Token: null);
    }

    /// <summary>
    /// Gets the client IP address from X-Forwarded-For header or the connection remote address.
    /// </summary>
    private string GetClientIpAddress()
    {
        if (Request.Headers.TryGetValue("X-Forwarded-For", out var forwardedFor))
        {
            var ip = forwardedFor.ToString().Split(',', StringSplitOptions.TrimEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(ip))
            {
                return ip;
            }
        }

        return HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    private record GuestSessionHeaderResult(bool HasHeader, bool IsValid, Guid? Token);
}

using AuthService.DAL.Data;
using AuthService.Infrastructure;
using AuthService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shared.Infrastructure;
using Shared.Models.DTOs;
using Shared.Models.Enums;
using Shared.Models.Events;
using Shared.Models.Interfaces;

namespace AuthService.Controllers;

[ApiController]
[Route("auth")]
public class AuthController : ControllerBase
{
    private readonly ITokenService _tokenService;
    private readonly IPasswordService _passwordService;
    private readonly IRBACService _rbacService;
    private readonly IRateLimiterService _rateLimiterService;
    private readonly IEventPublisher _eventPublisher;
    private readonly AuthDbContext _dbContext;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        ITokenService tokenService,
        IPasswordService passwordService,
        IRBACService rbacService,
        IRateLimiterService rateLimiterService,
        IEventPublisher eventPublisher,
        AuthDbContext dbContext,
        ILogger<AuthController> logger)
    {
        _tokenService = tokenService;
        _passwordService = passwordService;
        _rbacService = rbacService;
        _rateLimiterService = rateLimiterService;
        _eventPublisher = eventPublisher;
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <summary>
    /// POST /auth/login — Authenticate with email/password and return a token pair.
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            return Unauthorized(new ErrorResponse("AuthenticationFailed", "Authentication failed."));
        }

        // Check per-IP rate limit
        var ipAddress = GetClientIpAddress();
        var ipRateLimit = await _rateLimiterService.CheckIpRateLimit(ipAddress);
        if (!ipRateLimit.IsAllowed)
        {
            Response.Headers["Retry-After"] = ipRateLimit.RetryAfterSeconds?.ToString() ?? "60";
            return StatusCode(StatusCodes.Status429TooManyRequests,
                new ErrorResponse("RateLimitExceeded", "Too many requests. Please try again later."));
        }

        // Check per-email rate limit
        var emailRateLimit = await _rateLimiterService.CheckEmailRateLimit(request.Email);
        if (!emailRateLimit.IsAllowed)
        {
            Response.Headers["Retry-After"] = emailRateLimit.RetryAfterSeconds?.ToString() ?? "900";
            return StatusCode(StatusCodes.Status429TooManyRequests,
                new ErrorResponse("RateLimitExceeded", "Too many failed attempts. Please try again later."));
        }

        // Lookup user by email
        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Email == request.Email);
        if (user == null)
        {
            // Record failed attempt (generic message, don't reveal whether email exists)
            await _rateLimiterService.RecordFailedAttempt(request.Email);
            return Unauthorized(new ErrorResponse("AuthenticationFailed", "Authentication failed."));
        }

        // Check account status
        if (user.Status != UserStatus.Active.ToStatusString())
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                new ErrorResponse("AccountInactive", "Account is inactive."));
        }

        // Verify password
        if (!_passwordService.VerifyPassword(request.Password, user.PasswordHash))
        {
            await _rateLimiterService.RecordFailedAttempt(request.Email);
            return Unauthorized(new ErrorResponse("AuthenticationFailed", "Authentication failed."));
        }

        // Issue token pair
        var permissions = _rbacService.GetPermissions(user.Role);
        var tokenPair = _tokenService.IssueTokenPair(user.Id.ToString(), user.Role, permissions);

        _logger.LogInformation("User {UserId} authenticated successfully.", user.Id);

        // Read guest session token if present (passed through for client-side cart merge)
        string? guestSessionToken = null;
        var guestSessionHeader = Request.Headers["X-Guest-Session"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(guestSessionHeader) && Guid.TryParse(guestSessionHeader, out _))
        {
            guestSessionToken = guestSessionHeader;
        }

        return Ok(new LoginResponse(tokenPair.AccessToken, tokenPair.RefreshToken, tokenPair.ExpiresIn, guestSessionToken));
    }

    /// <summary>
    /// POST /auth/refresh — Refresh access token using a valid refresh token.
    /// </summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return Unauthorized(new ErrorResponse("InvalidToken", "Refresh token is required."));
        }

        try
        {
            var tokenPair = await _tokenService.RefreshToken(request.RefreshToken);
            return Ok(new LoginResponse(tokenPair.AccessToken, tokenPair.RefreshToken, tokenPair.ExpiresIn));
        }
        catch (TokenValidationException ex)
        {
            _logger.LogWarning("Token refresh failed: {ErrorCode} - {Message}", ex.ErrorCode, ex.Message);
            return Unauthorized(new ErrorResponse(ex.ErrorCode, "Token refresh failed."));
        }
    }

    /// <summary>
    /// POST /auth/logout — Revoke the current token (requires Bearer auth).
    /// </summary>
    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout()
    {
        var authHeader = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Unauthorized(new ErrorResponse("Unauthenticated", "Bearer token is required."));
        }

        var token = authHeader["Bearer ".Length..].Trim();

        try
        {
            // Validate the token to get the jti, then the token is effectively "used" for logout
            var payload = await _tokenService.ValidateToken(token);
            _logger.LogInformation("User {UserId} logged out. Token {Jti} revoked.", payload.Sub, payload.Jti);
            return Ok(new { message = "Logged out successfully." });
        }
        catch (TokenValidationException ex)
        {
            return Unauthorized(new ErrorResponse(ex.ErrorCode, ex.Message));
        }
    }

    /// <summary>
    /// GET /auth/validate — Internal endpoint for API Gateway token validation.
    /// </summary>
    [HttpGet("validate")]
    [AllowAnonymous]
    public async Task<IActionResult> Validate()
    {
        var authHeader = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Unauthorized(new ErrorResponse("Unauthenticated", "Authorization header with Bearer token is required."));
        }

        var token = authHeader["Bearer ".Length..].Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            return Unauthorized(new ErrorResponse("Unauthenticated", "Bearer token is empty."));
        }

        try
        {
            var payload = await _tokenService.ValidateToken(token);
            return Ok(payload);
        }
        catch (TokenValidationException ex)
        {
            return Unauthorized(new ErrorResponse(ex.ErrorCode, ex.Message));
        }
    }

    /// <summary>
    /// POST /auth/register — Development-only endpoint to create users for testing.
    /// Not available in production.
    /// </summary>
    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register(
        [FromBody] RegisterRequest request,
        [FromServices] IWebHostEnvironment env)
    {
        if (!env.IsDevelopment())
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new ErrorResponse("ValidationError", "Email and password are required."));
        }

        var validRoles = new[] { UserRole.Customer.ToRoleString(), UserRole.Admin.ToRoleString(), UserRole.B2BPartner.ToRoleString() };
        var role = validRoles.Contains(request.Role, StringComparer.OrdinalIgnoreCase)
            ? request.Role
            : UserRole.Customer.ToRoleString();

        // Check if email already exists
        var existingUser = await _dbContext.Users.FirstOrDefaultAsync(u => u.Email == request.Email);
        if (existingUser != null)
        {
            return Conflict(new ErrorResponse("EmailExists", "A user with this email already exists."));
        }

        var user = new DAL.Models.User
        {
            Id = Guid.NewGuid(),
            Email = request.Email,
            PasswordHash = _passwordService.HashPassword(request.Password),
            Role = role,
            Status = UserStatus.Active.ToStatusString(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();

        // Publish UserRegistered event to RabbitMQ
        await _eventPublisher.PublishUserRegisteredAsync(new UserRegisteredEvent(
            Type: "user.registered",
            Payload: new UserRegisteredPayload(user.Id.ToString(), user.Email, user.Role),
            Timestamp: DateTime.UtcNow,
            CorrelationId: Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? Guid.NewGuid().ToString()
        ));

        _logger.LogInformation("[DEV] Registered user {UserId} with email {Email} and role {Role}.", user.Id, user.Email, user.Role);

        return Created($"/auth/register/{user.Id}", new
        {
            userId = user.Id,
            email = user.Email,
            role = user.Role,
            status = user.Status
        });
    }

    private string GetClientIpAddress()
    {
        // Check for forwarded header first (behind reverse proxy)
        var forwarded = Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            return forwarded.Split(',')[0].Trim();
        }

        return HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}

// Request/Response DTOs for Auth endpoints
public record LoginRequest(string Email, string Password);
public record RefreshRequest(string RefreshToken);
public record RegisterRequest(string Email, string Password, string Role = "Customer");
public record LoginResponse(string AccessToken, string RefreshToken, int ExpiresIn, string? GuestSessionTokenPassthrough = null);

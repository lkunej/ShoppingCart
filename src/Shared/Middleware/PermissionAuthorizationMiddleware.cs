using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Shared.Models.Interfaces;

namespace Shared.Middleware;

/// <summary>
/// Middleware that performs endpoint-level authorization by validating
/// the role and permissions from JWT claims against the required permissions.
/// 
/// Use [RequirePermission("permission:name")] on controller actions or classes
/// to enforce RBAC checks.
/// </summary>
public class PermissionAuthorizationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IRBACService _rbacService;
    private readonly ILogger<PermissionAuthorizationMiddleware> _logger;

    public PermissionAuthorizationMiddleware(
        RequestDelegate next,
        IRBACService rbacService,
        ILogger<PermissionAuthorizationMiddleware> logger)
    {
        _next = next;
        _rbacService = rbacService;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var endpoint = context.GetEndpoint();

        // Check if the endpoint has a RequirePermission attribute
        var permissionAttribute = endpoint?.Metadata.GetMetadata<RequirePermissionAttribute>();
        if (permissionAttribute == null)
        {
            // No permission requirement on this endpoint; continue
            await _next(context);
            return;
        }

        // Extract role from claims (set by JWT authentication middleware)
        var user = context.User;
        if (user?.Identity == null || !user.Identity.IsAuthenticated)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Unauthorized", message = "User is not authenticated." });
            return;
        }

        var roleClaim = user.FindFirstValue("role") ?? user.FindFirstValue(ClaimTypes.Role);

        // Missing, empty, or unrecognized role claim -> 403
        if (string.IsNullOrWhiteSpace(roleClaim))
        {
            _logger.LogWarning("Request to {Path} denied: missing or empty role claim.", context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "Forbidden", message = "Missing or empty role claim." });
            return;
        }

        // Check if the role is recognized
        var permissions = _rbacService.GetPermissions(roleClaim);
        if (permissions.Length == 0)
        {
            _logger.LogWarning("Request to {Path} denied: unrecognized role '{Role}'.", context.Request.Path, roleClaim);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "Forbidden", message = "Unrecognized role." });
            return;
        }

        // Valid JWT but role doesn't have required permission -> 403
        var requiredPermission = permissionAttribute.Permission;
        if (!_rbacService.HasPermission(roleClaim, requiredPermission))
        {
            _logger.LogWarning(
                "Request to {Path} denied: role '{Role}' does not have required permission '{Permission}'.",
                context.Request.Path, roleClaim, requiredPermission);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "Forbidden", message = $"Role '{roleClaim}' does not have the required permission '{requiredPermission}'." });
            return;
        }

        await _next(context);
    }
}

/// <summary>
/// Attribute to mark endpoints that require a specific permission.
/// Apply to endpoint methods or controller classes to enforce RBAC permission checks.
/// 
/// Example:
///   [RequirePermission("cart:write")]
///   public async Task&lt;IActionResult&gt; AddItem(...) { ... }
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
public class RequirePermissionAttribute : Attribute
{
    public string Permission { get; }

    public RequirePermissionAttribute(string permission)
    {
        Permission = permission ?? throw new ArgumentNullException(nameof(permission));
    }
}

/// <summary>
/// Extension methods for registering the PermissionAuthorizationMiddleware.
/// </summary>
public static class PermissionAuthorizationMiddlewareExtensions
{
    /// <summary>
    /// Adds the permission-based authorization middleware to the pipeline.
    /// Must be placed after UseAuthentication() and UseAuthorization().
    /// </summary>
    public static IApplicationBuilder UsePermissionAuthorization(this IApplicationBuilder app)
    {
        return app.UseMiddleware<PermissionAuthorizationMiddleware>();
    }
}

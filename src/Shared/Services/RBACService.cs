using Shared.Models.Enums;
using Shared.Models.Interfaces;

namespace Shared.Services;

/// <summary>
/// Role-Based Access Control service that maps roles to permissions
/// and provides authorization checks.
/// Shared across all services that need to enforce permissions.
/// </summary>
public class RBACService : IRBACService
{
    private static readonly Dictionary<string, string[]> RolePermissions = new(StringComparer.OrdinalIgnoreCase)
    {
        [UserRole.Customer.ToRoleString()] = new[]
        {
            "cart:read",
            "cart:write"
        },
        [UserRole.Admin.ToRoleString()] = new[]
        {
            "cart:read",
            "cart:write",
            "admin:read",
            "admin:write",
            "users:manage"
        },
        [UserRole.B2BPartner.ToRoleString()] = new[]
        {
            "orders:bulk",
            "catalog:read",
            "cart:read",
            "cart:write"
        }
    };

    /// <inheritdoc />
    public bool HasPermission(string role, string permission)
    {
        if (string.IsNullOrWhiteSpace(role) || string.IsNullOrWhiteSpace(permission))
        {
            return false;
        }

        if (!RolePermissions.TryGetValue(role, out var permissions))
        {
            return false;
        }

        return permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public string[] GetPermissions(string role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return Array.Empty<string>();
        }

        if (!RolePermissions.TryGetValue(role, out var permissions))
        {
            return Array.Empty<string>();
        }

        return permissions;
    }
}

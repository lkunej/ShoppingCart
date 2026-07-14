namespace Shared.Models.Enums;

/// <summary>
/// Supported user roles in the platform.
/// </summary>
public enum UserRole
{
    Customer,
    Admin,
    B2BPartner
}

/// <summary>
/// Extension methods for UserRole enum.
/// </summary>
public static class UserRoleExtensions
{
    /// <summary>
    /// Converts the enum to the string representation stored in the database.
    /// </summary>
    public static string ToRoleString(this UserRole role) => role switch
    {
        UserRole.Customer => "Customer",
        UserRole.Admin => "Admin",
        UserRole.B2BPartner => "B2BPartner",
        _ => "Customer"
    };

    /// <summary>
    /// Parses a string to a UserRole. Returns null if the string is not a valid role.
    /// </summary>
    public static UserRole? ParseRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role)) return null;

        return role switch
        {
            "Customer" => UserRole.Customer,
            "Admin" => UserRole.Admin,
            "B2BPartner" => UserRole.B2BPartner,
            _ => null
        };
    }

    /// <summary>
    /// Returns true if the string represents a valid UserRole.
    /// </summary>
    public static bool IsValidRole(string? role)
    {
        return ParseRole(role) is not null;
    }
}

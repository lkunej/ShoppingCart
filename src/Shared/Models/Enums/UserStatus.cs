namespace Shared.Models.Enums;

/// <summary>
/// Supported user account statuses.
/// </summary>
public enum UserStatus
{
    Active,
    Disabled,
    Locked
}

/// <summary>
/// Extension methods for UserStatus enum.
/// </summary>
public static class UserStatusExtensions
{
    /// <summary>
    /// Converts the enum to the lowercase string representation stored in the database.
    /// </summary>
    public static string ToStatusString(this UserStatus status) => status switch
    {
        UserStatus.Active => "active",
        UserStatus.Disabled => "disabled",
        UserStatus.Locked => "locked",
        _ => "active"
    };

    /// <summary>
    /// Parses a string to a UserStatus. Returns null if the string is not a valid status.
    /// </summary>
    public static UserStatus? ParseStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return null;

        return status.ToLowerInvariant() switch
        {
            "active" => UserStatus.Active,
            "disabled" => UserStatus.Disabled,
            "locked" => UserStatus.Locked,
            _ => null
        };
    }

    /// <summary>
    /// Returns true if the string represents a valid UserStatus.
    /// </summary>
    public static bool IsValidStatus(string? status)
    {
        return ParseStatus(status) is not null;
    }
}

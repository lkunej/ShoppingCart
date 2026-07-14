namespace Shared.Models.Interfaces;

public interface IRBACService
{
    /// <summary>
    /// Checks whether the given role has the specified permission.
    /// </summary>
    bool HasPermission(string role, string permission);

    /// <summary>
    /// Returns all permissions for the given role.
    /// </summary>
    string[] GetPermissions(string role);
}

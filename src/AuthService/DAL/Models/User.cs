using Shared.Models.Enums;

namespace AuthService.DAL.Models;

public class User
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = UserRole.Customer.ToRoleString();
    public string Status { get; set; } = UserStatus.Active.ToStatusString();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

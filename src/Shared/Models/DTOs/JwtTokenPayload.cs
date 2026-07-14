namespace Shared.Models.DTOs;

public class JwtTokenPayload
{
    public string Sub { get; set; } = string.Empty;       // userId (UUID)
    public string Role { get; set; } = string.Empty;      // "Customer" | "Admin" | "B2BPartner"
    public string[] Permissions { get; set; } = [];       // e.g. ["cart:read", "cart:write"]
    public long Iat { get; set; }                         // issued at (epoch seconds)
    public long Exp { get; set; }                         // expiration (epoch seconds)
    public string Jti { get; set; } = string.Empty;       // unique token ID (UUID)
}

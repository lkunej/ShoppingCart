using CartService.Models.DTOs;

namespace CartService.Services;

/// <summary>
/// Result of a cart merge operation.
/// </summary>
public record MergeResult(MergeResponse Response, bool Success);

/// <summary>
/// Service responsible for merging guest cart items into an authenticated user's cart.
/// </summary>
public interface ICartMergeService
{
    /// <summary>
    /// Merges the guest cart identified by the session token into the authenticated user's cart.
    /// Uses max-quantity conflict resolution (capped at 9999), respects 50-item limit,
    /// and runs stock validation pass when inventory service is available.
    /// After successful merge, the guest cart is deleted and the session token is invalidated.
    /// </summary>
    Task<MergeResult> MergeGuestCart(Guid userId, Guid guestSessionToken);
}

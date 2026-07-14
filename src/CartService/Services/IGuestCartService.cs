using CartService.DAL.Models;
using CartService.Models.DTOs;

namespace CartService.Services;

public interface IGuestCartService
{
    /// <summary>
    /// Creates a new guest session with an empty cart.
    /// Generates a new UUID v4 guest session token.
    /// </summary>
    Task<GuestCartResponse> CreateSession();

    /// <summary>
    /// Retrieves the guest cart for the specified session token.
    /// Throws KeyNotFoundException if no cart exists for the token.
    /// </summary>
    Task<GuestCartResponse> GetCart(Guid guestSessionToken);

    /// <summary>
    /// Adds an item to the guest cart. Sets exact quantity if product already exists (not additive).
    /// </summary>
    Task<GuestCartResponse> AddItem(Guid guestSessionToken, Guid productId, int quantity);

    /// <summary>
    /// Updates the quantity of an existing item in the guest cart.
    /// </summary>
    Task<GuestCartResponse> UpdateItem(Guid guestSessionToken, Guid itemId, int quantity);

    /// <summary>
    /// Removes an item from the guest cart.
    /// </summary>
    Task<GuestCartResponse> RemoveItem(Guid guestSessionToken, Guid itemId);

    /// <summary>
    /// Retrieves the raw Cart entity for the specified guest session token.
    /// Used internally for cart merge operations.
    /// </summary>
    Task<Cart?> GetCartEntity(Guid guestSessionToken);

    /// <summary>
    /// Deletes the guest cart and its associated items.
    /// Used after successful cart merge on login.
    /// </summary>
    Task DeleteGuestCart(Guid guestSessionToken);
}

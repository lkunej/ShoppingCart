using Shared.Models.DTOs;

namespace Shared.Models.Interfaces;

public interface ICartService
{
    /// <summary>
    /// Retrieves the cart for the specified user.
    /// Uses cache-aside pattern: Redis first, then PostgreSQL fallback.
    /// </summary>
    Task<CartDto> GetCart(Guid userId);

    /// <summary>
    /// Adds an item to the user's cart. Sets exact quantity if product already exists (not additive).
    /// </summary>
    Task<CartDto> AddItem(Guid userId, Guid productId, int quantity);

    /// <summary>
    /// Updates the quantity of an existing cart item (sets exact quantity, not increment).
    /// </summary>
    Task<CartDto> UpdateItem(Guid userId, Guid itemId, int newQuantity);

    /// <summary>
    /// Removes an item from the user's cart.
    /// </summary>
    Task<CartDto> RemoveItem(Guid userId, Guid itemId);

    /// <summary>
    /// Clears the user's cart completely (deletes from DB + invalidates Redis cache).
    /// Used after successful checkout.
    /// </summary>
    Task ClearCart(Guid userId);
}

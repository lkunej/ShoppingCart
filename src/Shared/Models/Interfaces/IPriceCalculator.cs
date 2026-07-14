using Shared.Models.DTOs;

namespace Shared.Models.Interfaces;

public interface IPriceCalculator
{
    /// <summary>
    /// Calculates the total price for a collection of cart items using integer arithmetic (cents).
    /// All items must have currency EUR. Enforces maximum cart total and item count limits.
    /// </summary>
    MoneyDto CalculateTotal(IEnumerable<CartItemDto> items);
}

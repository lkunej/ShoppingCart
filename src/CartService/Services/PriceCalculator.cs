using Shared.Models.DTOs;
using Shared.Models.Interfaces;

namespace CartService.Services;

public class PriceCalculator : IPriceCalculator
{
    private const int MaxCartTotalCents = 999_999_999;
    private const int MaxDistinctItems = 100;
    private const string RequiredCurrency = "EUR";

    /// <inheritdoc />
    public MoneyDto CalculateTotal(IEnumerable<CartItemDto> items)
    {
        if (items is null)
        {
            return new MoneyDto(0, RequiredCurrency);
        }

        var itemList = items.ToList();

        if (itemList.Count == 0)
        {
            return new MoneyDto(0, RequiredCurrency);
        }

        if (itemList.Count > MaxDistinctItems)
        {
            throw new InvalidOperationException(
                $"Cart cannot contain more than {MaxDistinctItems} distinct items. Current count: {itemList.Count}.");
        }

        // Validate all items have EUR currency
        for (int i = 0; i < itemList.Count; i++)
        {
            var item = itemList[i];
            if (!string.Equals(item.UnitPrice.Currency, RequiredCurrency, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Currency mismatch: item '{item.ProductName}' has currency '{item.UnitPrice.Currency}' but only '{RequiredCurrency}' is supported.");
            }
        }

        // Calculate total using long to avoid integer overflow during intermediate sums
        long total = 0;
        foreach (var item in itemList)
        {
            long lineTotal = (long)item.UnitPrice.Amount * item.Quantity;
            total += lineTotal;
        }

        if (total > MaxCartTotalCents)
        {
            throw new InvalidOperationException(
                $"Cart total {total} cents exceeds maximum allowed total of {MaxCartTotalCents} cents.");
        }

        return new MoneyDto((int)total, RequiredCurrency);
    }
}

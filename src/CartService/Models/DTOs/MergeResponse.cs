using Shared.Models.DTOs;

namespace CartService.Models.DTOs;

public record MergeResponse(
    Guid CartId,
    Guid UserId,
    List<CartItemDto> Items,
    MoneyDto TotalPrice,
    List<MergeAdjustment>? Adjustments,
    bool StockValidationSkipped,
    bool CartItemLimitReached
);

using Shared.Models.DTOs;

namespace CartService.Models.DTOs;

public record GuestCartResponse(
    Guid GuestSessionToken,
    Guid CartId,
    List<CartItemDto> Items,
    MoneyDto TotalPrice,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

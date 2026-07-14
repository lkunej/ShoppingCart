namespace Shared.Models.DTOs;

public record CartDto(
    Guid Id,
    Guid UserId,
    List<CartItemDto> Items,
    MoneyDto TotalPrice,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int SchemaVersion
);

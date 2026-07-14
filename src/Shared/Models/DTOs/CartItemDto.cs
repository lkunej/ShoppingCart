namespace Shared.Models.DTOs;

public record CartItemDto(
    Guid Id,
    Guid ProductId,
    string ProductName,
    MoneyDto UnitPrice,
    int Quantity
);

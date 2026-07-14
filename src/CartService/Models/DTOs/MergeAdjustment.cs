namespace CartService.Models.DTOs;

public record MergeAdjustment(
    Guid ProductId,
    string ProductName,
    int OriginalGuestQuantity,
    int OriginalAuthQuantity,
    int MergedQuantity,
    string Reason
);

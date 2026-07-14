namespace Shared.Models.Events;

public record CartClearedEvent(
    string Type,        // "cart.cleared"
    CartClearedPayload Payload,
    DateTime Timestamp,
    string CorrelationId
);

public record CartClearedPayload(string UserId);

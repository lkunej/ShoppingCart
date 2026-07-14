namespace Shared.Models.Events;

public record InventoryUpdatedEvent(
    string Type,        // "inventory.updated"
    InventoryUpdatedPayload Payload,
    DateTime Timestamp,
    string CorrelationId
);

public record InventoryUpdatedPayload(string ProductId, int AvailableQuantity);

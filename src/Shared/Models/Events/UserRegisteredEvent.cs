namespace Shared.Models.Events;

public record UserRegisteredEvent(
    string Type,        // "user.registered"
    UserRegisteredPayload Payload,
    DateTime Timestamp,
    string CorrelationId
);

public record UserRegisteredPayload(string UserId, string Email, string Role);

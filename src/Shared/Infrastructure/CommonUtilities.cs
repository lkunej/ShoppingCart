namespace Shared.Infrastructure;

/// <summary>
/// Common utility helpers shared across services.
/// </summary>
public static class CommonUtilities
{
    /// <summary>
    /// Generates a new correlation ID as a UUID string.
    /// Used for distributed tracing across service calls.
    /// </summary>
    public static string GenerateCorrelationId()
    {
        return Guid.NewGuid().ToString();
    }
}

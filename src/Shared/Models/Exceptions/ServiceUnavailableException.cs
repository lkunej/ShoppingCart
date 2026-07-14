namespace Shared.Models.Exceptions;

/// <summary>
/// Thrown when a downstream service is unavailable (e.g., circuit breaker is open).
/// Indicates the caller should return HTTP 503 Service Unavailable.
/// </summary>
public class ServiceUnavailableException : Exception
{
    public string ServiceName { get; }

    public ServiceUnavailableException(string serviceName)
        : base($"Service '{serviceName}' is currently unavailable. Circuit breaker is open.")
    {
        ServiceName = serviceName;
    }

    public ServiceUnavailableException(string serviceName, Exception innerException)
        : base($"Service '{serviceName}' is currently unavailable. Circuit breaker is open.", innerException)
    {
        ServiceName = serviceName;
    }
}

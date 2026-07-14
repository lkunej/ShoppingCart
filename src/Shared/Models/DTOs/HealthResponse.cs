namespace Shared.Models.DTOs;

public record HealthResponse(
    string Status,                    // "Healthy" | "Degraded" | "Unhealthy"
    List<DependencyHealth> Dependencies,
    DateTime Timestamp
);

public record DependencyHealth(
    string Name,
    string Status,                    // "Healthy" | "Degraded" | "Unhealthy"
    long ResponseTimeMs
);

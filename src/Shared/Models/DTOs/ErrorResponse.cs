namespace Shared.Models.DTOs;

public record ErrorResponse(string Error, string? Message = null, string? Detail = null);

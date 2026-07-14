using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace AuthService.Infrastructure;

/// <summary>
/// Adds the X-Guest-Session header parameter to the login endpoint in Swagger UI.
/// </summary>
public class GuestSessionHeaderOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var path = context.ApiDescription.RelativePath ?? string.Empty;

        // Only apply to the login endpoint
        if (!path.Equals("auth/login", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        operation.Parameters ??= new List<OpenApiParameter>();

        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "X-Guest-Session",
            In = ParameterLocation.Header,
            Required = false,
            Description = "Guest session token (UUID) to pass through for cart merge after login.",
            Schema = new OpenApiSchema
            {
                Type = "string",
                Format = "uuid"
            }
        });
    }
}

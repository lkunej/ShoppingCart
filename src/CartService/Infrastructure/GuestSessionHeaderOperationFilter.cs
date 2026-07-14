using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace CartService.Infrastructure;

/// <summary>
/// Adds the X-Guest-Session header parameter to Swagger UI for guest cart endpoints
/// and the merge endpoint.
/// </summary>
public class GuestSessionHeaderOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var path = context.ApiDescription.RelativePath ?? string.Empty;

        // Apply to guest-cart endpoints and the cart/merge endpoint
        if (!path.StartsWith("api/guest-cart", StringComparison.OrdinalIgnoreCase) &&
            !path.Equals("api/cart/merge", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        operation.Parameters ??= new List<OpenApiParameter>();

        // Don't add if already present
        if (operation.Parameters.Any(p =>
            p.Name.Equals("X-Guest-Session", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "X-Guest-Session",
            In = ParameterLocation.Header,
            Required = false,
            Description = "Guest session token (UUID). Omit for first request to create a new session. " +
                          "Use the guestSessionToken from the response for subsequent requests.",
            Schema = new OpenApiSchema
            {
                Type = "string",
                Format = "uuid"
            }
        });
    }
}

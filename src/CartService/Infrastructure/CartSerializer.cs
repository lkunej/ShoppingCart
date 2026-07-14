using System.Text.Json;
using Microsoft.Extensions.Logging;
using Shared.Models.DTOs;

namespace CartService.Infrastructure;

/// <summary>
/// Interface for cart serialization with schema versioning support.
/// </summary>
public interface ICartSerializer
{
    /// <summary>
    /// Serializes a CartDto to JSON using the current schema version.
    /// </summary>
    string Serialize(CartDto cart);

    /// <summary>
    /// Deserializes a JSON string to CartDto, supporting current and previous schema versions.
    /// Returns null if the data is malformed or has an unrecognized schema version,
    /// logging an error with the userId and schema version.
    /// </summary>
    CartDto? Deserialize(string json, Guid userId);
}

/// <summary>
/// JSON serializer for CartDto with schema versioning.
/// Supports current version (V1) and previous version (V0).
/// Malformed or unrecognized versions return null (caller falls back to PostgreSQL).
/// </summary>
public class CartSerializer : ICartSerializer
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly ILogger<CartSerializer> _logger;

    public CartSerializer(ILogger<CartSerializer> logger)
    {
        _logger = logger;
    }

    public string Serialize(CartDto cart)
    {
        // Always serialize with the current schema version
        var dtoWithVersion = cart with { SchemaVersion = CurrentSchemaVersion };
        return JsonSerializer.Serialize(dtoWithVersion, JsonOptions);
    }

    public CartDto? Deserialize(string json, Guid userId)
    {
        try
        {
            // First, extract the schema version from the JSON
            var versionInfo = JsonSerializer.Deserialize<SchemaVersionEnvelope>(json, JsonOptions);

            if (versionInfo is null)
            {
                _logger.LogError(
                    "Failed to deserialize cart cache entry for user {UserId}: null result during schema version extraction.",
                    userId);
                return null;
            }

            var schemaVersion = versionInfo.SchemaVersion;

            if (schemaVersion == CurrentSchemaVersion)
            {
                // Current version: deserialize directly
                return JsonSerializer.Deserialize<CartDto>(json, JsonOptions);
            }

            if (schemaVersion == CurrentSchemaVersion - 1)
            {
                // Previous version (V0): deserialize from V0 shape and migrate to current
                return MigrateFromV0(json, userId);
            }

            // Unrecognized schema version
            _logger.LogError(
                "Unrecognized schema version {SchemaVersion} in cart cache entry for user {UserId}. Discarding cached entry.",
                schemaVersion, userId);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex,
                "Malformed JSON in cart cache entry for user {UserId}. Discarding cached entry.",
                userId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Unexpected error deserializing cart cache entry for user {UserId}. Discarding cached entry.",
                userId);
            return null;
        }
    }

    private CartDto? MigrateFromV0(string json, Guid userId)
    {
        try
        {
            var v0Cart = JsonSerializer.Deserialize<CartDtoV0>(json, JsonOptions);

            if (v0Cart is null)
            {
                _logger.LogError(
                    "Failed to deserialize V0 cart cache entry for user {UserId}.",
                    userId);
                return null;
            }

            // Migrate V0 items to current format
            var migratedItems = v0Cart.Items.Select(v0Item => new CartItemDto(
                v0Item.Id,
                v0Item.ProductId,
                v0Item.ProductName ?? string.Empty, // V0 may have null ProductName
                v0Item.UnitPrice ?? new MoneyDto(v0Item.UnitPriceAmount ?? 0, "EUR"), // V0 had flat UnitPriceAmount
                v0Item.Quantity
            )).ToList();

            return new CartDto(
                v0Cart.Id,
                v0Cart.UserId,
                migratedItems,
                v0Cart.TotalPrice,
                v0Cart.CreatedAt,
                v0Cart.UpdatedAt,
                CurrentSchemaVersion // Migrated to current version
            );
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex,
                "Failed to migrate V0 cart cache entry for user {UserId}. Schema version: 0. Discarding cached entry.",
                userId);
            return null;
        }
    }

    #region Schema Version DTOs

    /// <summary>
    /// Minimal envelope to extract schema version from JSON without full deserialization.
    /// </summary>
    private record SchemaVersionEnvelope(int SchemaVersion);

    /// <summary>
    /// V0 cart DTO shape. In V0, cart items had a flat UnitPriceAmount integer
    /// instead of the nested MoneyDto UnitPrice object, and ProductName could be null.
    /// </summary>
    private record CartDtoV0(
        Guid Id,
        Guid UserId,
        List<CartItemDtoV0> Items,
        MoneyDto TotalPrice,
        DateTime CreatedAt,
        DateTime UpdatedAt,
        int SchemaVersion
    );

    /// <summary>
    /// V0 cart item shape. Had a flat UnitPriceAmount (int) field instead of nested UnitPrice (MoneyDto).
    /// ProductName was optional (nullable).
    /// </summary>
    private record CartItemDtoV0(
        Guid Id,
        Guid ProductId,
        string? ProductName,
        MoneyDto? UnitPrice,
        int Quantity,
        int? UnitPriceAmount = null
    );

    #endregion
}

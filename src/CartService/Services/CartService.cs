using CartService.DAL.Data;
using CartService.DAL.Models;
using CartService.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shared.Infrastructure;
using Shared.Models.DTOs;
using Shared.Models.Events;
using Shared.Models.Interfaces;

namespace CartService.Services;

public class CartService : ICartService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    private readonly CartDbContext _dbContext;
    private readonly ICartRedisWrapper _redisWrapper;
    private readonly IInventoryClient _inventoryClient;
    private readonly IPriceCalculator _priceCalculator;
    private readonly ICartSerializer _cartSerializer;
    private readonly ICartEventPublisher _eventPublisher;
    private readonly ILogger<CartService> _logger;

    public CartService(
        CartDbContext dbContext,
        ICartRedisWrapper redisWrapper,
        IInventoryClient inventoryClient,
        IPriceCalculator priceCalculator,
        ICartSerializer cartSerializer,
        ICartEventPublisher eventPublisher,
        ILogger<CartService> logger)
    {
        _dbContext = dbContext;
        _redisWrapper = redisWrapper;
        _inventoryClient = inventoryClient;
        _priceCalculator = priceCalculator;
        _cartSerializer = cartSerializer;
        _eventPublisher = eventPublisher;
        _logger = logger;
    }

    public async Task<CartDto> GetCart(Guid userId)
    {
        // Cache-aside: try Redis first
        var cachedCart = await TryGetFromCache(userId);
        if (cachedCart is not null)
        {
            _logger.LogInformation("Cache HIT for user {UserId}. Returning cart from Redis.", userId);
            return cachedCart;
        }

        _logger.LogInformation("Cache MISS for user {UserId}. Querying PostgreSQL.", userId);

        // Fallback to PostgreSQL
        var cart = await _dbContext.Carts
            .Include(c => c.Items)
            .AsSplitQuery()
            .FirstOrDefaultAsync(c => c.UserId == userId);

        if (cart is null)
        {
            // No cart exists — return empty representation
            return CreateEmptyCartDto(userId);
        }

        var cartDto = MapToDto(cart);

        // Populate Redis on DB read
        await TryWriteToCache(userId, cartDto);

        return cartDto;
    }

    public async Task<CartDto> AddItem(Guid userId, Guid productId, int quantity)
    {
        var cart = await _dbContext.Carts
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.UserId == userId);

        if (cart is null)
        {
            // Create new cart
            cart = new Cart
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _dbContext.Carts.Add(cart);
        }

        // Single query: check availability AND get product info (name, price)
        var productInfo = await _inventoryClient.CheckAvailabilityWithProductInfo(productId, quantity);

        var existingItem = cart.Items.FirstOrDefault(i => i.ProductId == productId);

        if (existingItem is not null)
        {
            // Product already in cart — set to the requested quantity (absolute, not additive)
            existingItem.Quantity = quantity;
            existingItem.ProductName = productInfo.ProductName;
            existingItem.UnitPriceAmount = productInfo.UnitPriceAmount;
            existingItem.UnitPriceCurrency = productInfo.UnitPriceCurrency;
            existingItem.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            // Add new item with product info from inventory
            var newItem = new CartItem
            {
                CartId = cart.Id,
                ProductId = productId,
                ProductName = productInfo.ProductName,
                UnitPriceAmount = productInfo.UnitPriceAmount,
                UnitPriceCurrency = productInfo.UnitPriceCurrency,
                Quantity = quantity,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            cart.Items.Add(newItem);
        }

        cart.UpdatedAt = DateTime.UtcNow;

        // Persist to PostgreSQL first
        await _dbContext.SaveChangesAsync();

        var cartDto = MapToDto(cart);

        // Then write to Redis
        await TryWriteToCache(userId, cartDto);

        return cartDto;
    }

    public async Task<CartDto> UpdateItem(Guid userId, Guid itemId, int newQuantity)
    {
        var cart = await _dbContext.Carts
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.UserId == userId);

        if (cart is null)
        {
            throw new KeyNotFoundException($"Cart not found for user {userId}.");
        }

        var item = cart.Items.FirstOrDefault(i => i.Id == itemId);
        if (item is null)
        {
            throw new KeyNotFoundException($"Item {itemId} not found in cart.");
        }

        // Single query: verify availability AND refresh product info (price may have changed)
        var productInfo = await _inventoryClient.CheckAvailabilityWithProductInfo(item.ProductId, newQuantity);

        item.ProductName = productInfo.ProductName;
        item.UnitPriceAmount = productInfo.UnitPriceAmount;
        item.UnitPriceCurrency = productInfo.UnitPriceCurrency;

        // Set exact quantity (not increment)
        item.Quantity = newQuantity;
        item.UpdatedAt = DateTime.UtcNow;
        cart.UpdatedAt = DateTime.UtcNow;

        // Persist to PostgreSQL first
        await _dbContext.SaveChangesAsync();

        var cartDto = MapToDto(cart);

        // Then write to Redis
        await TryWriteToCache(userId, cartDto);

        return cartDto;
    }

    public async Task<CartDto> RemoveItem(Guid userId, Guid itemId)
    {
        var cart = await _dbContext.Carts
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.UserId == userId);

        if (cart is null)
        {
            throw new KeyNotFoundException($"Cart not found for user {userId}.");
        }

        var item = cart.Items.FirstOrDefault(i => i.Id == itemId);
        if (item is null)
        {
            throw new KeyNotFoundException($"Item {itemId} not found in cart.");
        }

        // Remove the item from PostgreSQL first
        _dbContext.CartItems.Remove(item);
        cart.Items.Remove(item);

        if (cart.Items.Count == 0)
        {
            // Last item removed — delete the entire cart entity
            _dbContext.Carts.Remove(cart);
            await _dbContext.SaveChangesAsync();

            // Invalidate Redis cache and untrack all products
            await TryRemoveFromCache(userId, new[] { item.ProductId });

            return CreateEmptyCartDto(userId);
        }

        cart.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();

        var cartDto = MapToDto(cart);

        // Write updated cart (will re-register remaining product tracking)
        await TryWriteToCache(userId, cartDto);

        // Untrack the removed product
        await _redisWrapper.UntrackProductsInCartAsync(userId, new[] { item.ProductId });

        return cartDto;
    }

    public async Task ClearCart(Guid userId)
    {
        var cart = await _dbContext.Carts
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.UserId == userId);

        List<Guid> productIds = new();

        if (cart is not null)
        {
            productIds = cart.Items.Select(i => i.ProductId).ToList();
            _dbContext.CartItems.RemoveRange(cart.Items);
            _dbContext.Carts.Remove(cart);
            await _dbContext.SaveChangesAsync();

            _logger.LogInformation("Cart cleared for user {UserId} after checkout.", userId);
        }

        // Invalidate Redis cache and untrack all products
        await TryRemoveFromCache(userId, productIds);

        // Publish CartCleared event to RabbitMQ
        await _eventPublisher.PublishCartClearedAsync(new CartClearedEvent(
            Type: "cart.cleared",
            Payload: new CartClearedPayload(userId.ToString()),
            Timestamp: DateTime.UtcNow,
            CorrelationId: Guid.NewGuid().ToString()
        ));
    }

    #region Cache Operations

    private async Task<CartDto?> TryGetFromCache(Guid userId)
    {
        try
        {
            var json = await _redisWrapper.GetCartAsync(userId);

            if (json is null)
            {
                return null;
            }

            var cartDto = _cartSerializer.Deserialize(json, userId);

            if (cartDto is null)
            {
                // Malformed or unrecognized schema version — discard cached entry
                await _redisWrapper.DeleteCartAsync(userId);
            }

            return cartDto;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error reading from Redis cache for user {UserId}. Falling back to PostgreSQL.", userId);
            return null;
        }
    }

    private async Task TryWriteToCache(Guid userId, CartDto cartDto)
    {
        try
        {
            var json = _cartSerializer.Serialize(cartDto);
            await _redisWrapper.SetCartAsync(userId, json, CacheTtl);

            // Maintain the product→user reverse index for efficient invalidation
            foreach (var item in cartDto.Items)
            {
                await _redisWrapper.TrackProductInCartAsync(userId, item.ProductId, CacheTtl);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error writing to Redis cache for user {UserId}. Cache will be repopulated on next read.", userId);
        }
    }

    private async Task TryRemoveFromCache(Guid userId, IEnumerable<Guid>? productIds = null)
    {
        try
        {
            await _redisWrapper.DeleteCartAsync(userId);

            // Clean up reverse index entries
            if (productIds is not null)
            {
                await _redisWrapper.UntrackProductsInCartAsync(userId, productIds);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error invalidating Redis cache for user {UserId}.", userId);
        }
    }

    private static string GetCacheKey(Guid userId) => $"cart:{userId}";

    #endregion

    #region Mapping

    private CartDto MapToDto(Cart cart)
    {
        var items = cart.Items.Select(i => new CartItemDto(
            i.Id,
            i.ProductId,
            i.ProductName,
            new MoneyDto(i.UnitPriceAmount, i.UnitPriceCurrency),
            i.Quantity
        )).ToList();

        var totalPrice = _priceCalculator.CalculateTotal(items);

        return new CartDto(
            cart.Id,
            cart.UserId,
            items,
            totalPrice,
            cart.CreatedAt,
            cart.UpdatedAt,
            CartSerializer.CurrentSchemaVersion
        );
    }

    private static CartDto CreateEmptyCartDto(Guid userId)
    {
        return new CartDto(
            Guid.Empty,
            userId,
            new List<CartItemDto>(),
            new MoneyDto(0, "EUR"),
            DateTime.UtcNow,
            DateTime.UtcNow,
            CartSerializer.CurrentSchemaVersion
        );
    }

    #endregion
}

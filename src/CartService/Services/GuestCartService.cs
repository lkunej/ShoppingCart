using CartService.DAL.Data;
using CartService.DAL.Models;
using CartService.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shared.Models.DTOs;
using Shared.Models.Interfaces;
using CartService.Models.DTOs;

namespace CartService.Services;

public class GuestCartService : IGuestCartService
{
    private static readonly TimeSpan GuestCacheTtl = TimeSpan.FromDays(10);
    private const int MaxGuestCartItems = 50;
    private const int MaxQuantityPerItem = 9999;

    private readonly CartDbContext _dbContext;
    private readonly ICartRedisWrapper _redisWrapper;
    private readonly IInventoryClient _inventoryClient;
    private readonly IPriceCalculator _priceCalculator;
    private readonly ICartSerializer _cartSerializer;
    private readonly ILogger<GuestCartService> _logger;

    public GuestCartService(
        CartDbContext dbContext,
        ICartRedisWrapper redisWrapper,
        IInventoryClient inventoryClient,
        IPriceCalculator priceCalculator,
        ICartSerializer cartSerializer,
        ILogger<GuestCartService> logger)
    {
        _dbContext = dbContext;
        _redisWrapper = redisWrapper;
        _inventoryClient = inventoryClient;
        _priceCalculator = priceCalculator;
        _cartSerializer = cartSerializer;
        _logger = logger;
    }

    public async Task<GuestCartResponse> CreateSession()
    {
        var newToken = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var cart = new Cart
        {
            UserId = Guid.Empty,
            GuestSessionToken = newToken,
            CreatedAt = now,
            UpdatedAt = now
        };

        _dbContext.Carts.Add(cart);
        await _dbContext.SaveChangesAsync();

        var cartDto = MapToCartDto(cart);
        await TryWriteToCache(newToken, cartDto);

        return MapToGuestCartResponse(newToken, cart);
    }

    public async Task<GuestCartResponse> GetCart(Guid guestSessionToken)
    {
        // Cache-aside: try Redis first
        var cachedCart = await TryGetFromCache(guestSessionToken);
        if (cachedCart is not null)
        {
            _logger.LogInformation("Cache HIT for guest session {Token}. Returning cart from Redis.", guestSessionToken);
            return MapCachedToGuestCartResponse(guestSessionToken, cachedCart);
        }

        _logger.LogInformation("Cache MISS for guest session {Token}. Querying PostgreSQL.", guestSessionToken);

        // Fallback to PostgreSQL
        var cart = await _dbContext.Carts
            .Include(c => c.Items)
            .AsSplitQuery()
            .FirstOrDefaultAsync(c => c.GuestSessionToken == guestSessionToken);

        if (cart is null)
        {
            throw new KeyNotFoundException($"Guest cart not found for session {guestSessionToken}.");
        }

        var cartDto = MapToCartDto(cart);

        // Repopulate cache on miss
        await TryWriteToCache(guestSessionToken, cartDto);

        return MapToGuestCartResponse(guestSessionToken, cart);
    }

    public async Task<GuestCartResponse> AddItem(Guid guestSessionToken, Guid productId, int quantity)
    {
        if (quantity < 1 || quantity > MaxQuantityPerItem)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), $"Quantity must be between 1 and {MaxQuantityPerItem}.");
        }

        var cart = await _dbContext.Carts
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.GuestSessionToken == guestSessionToken);

        if (cart is null)
        {
            // Create a new cart for this guest session
            cart = new Cart
            {
                UserId = Guid.Empty,
                GuestSessionToken = guestSessionToken,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _dbContext.Carts.Add(cart);
        }

        // Check 50-item limit (only for new products, not updates to existing)
        var existingItem = cart.Items.FirstOrDefault(i => i.ProductId == productId);
        if (existingItem is null && cart.Items.Count >= MaxGuestCartItems)
        {
            throw new InvalidOperationException($"Guest cart cannot exceed {MaxGuestCartItems} distinct items.");
        }

        // Verify stock and get product info
        var productInfo = await _inventoryClient.CheckAvailabilityWithProductInfo(productId, quantity);

        if (existingItem is not null)
        {
            // Product already in cart — set to the requested quantity (not additive)
            existingItem.Quantity = quantity;
            existingItem.ProductName = productInfo.ProductName;
            existingItem.UnitPriceAmount = productInfo.UnitPriceAmount;
            existingItem.UnitPriceCurrency = productInfo.UnitPriceCurrency;
            existingItem.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            // Add new item
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
        await _dbContext.SaveChangesAsync();

        var cartDto = MapToCartDto(cart);

        // Update Redis cache and reset TTL
        await TryWriteToCache(guestSessionToken, cartDto);

        return MapToGuestCartResponse(guestSessionToken, cart);
    }

    public async Task<GuestCartResponse> UpdateItem(Guid guestSessionToken, Guid itemId, int quantity)
    {
        if (quantity < 1 || quantity > MaxQuantityPerItem)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), $"Quantity must be between 1 and {MaxQuantityPerItem}.");
        }

        var cart = await _dbContext.Carts
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.GuestSessionToken == guestSessionToken);

        if (cart is null)
        {
            throw new KeyNotFoundException($"Guest cart not found for session {guestSessionToken}.");
        }

        var item = cart.Items.FirstOrDefault(i => i.Id == itemId);
        if (item is null)
        {
            throw new KeyNotFoundException($"Item {itemId} not found in guest cart.");
        }

        // Verify stock and refresh product info
        var productInfo = await _inventoryClient.CheckAvailabilityWithProductInfo(item.ProductId, quantity);

        item.Quantity = quantity;
        item.ProductName = productInfo.ProductName;
        item.UnitPriceAmount = productInfo.UnitPriceAmount;
        item.UnitPriceCurrency = productInfo.UnitPriceCurrency;
        item.UpdatedAt = DateTime.UtcNow;
        cart.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        var cartDto = MapToCartDto(cart);

        // Update Redis cache and reset TTL
        await TryWriteToCache(guestSessionToken, cartDto);

        return MapToGuestCartResponse(guestSessionToken, cart);
    }

    public async Task<GuestCartResponse> RemoveItem(Guid guestSessionToken, Guid itemId)
    {
        var cart = await _dbContext.Carts
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.GuestSessionToken == guestSessionToken);

        if (cart is null)
        {
            throw new KeyNotFoundException($"Guest cart not found for session {guestSessionToken}.");
        }

        var item = cart.Items.FirstOrDefault(i => i.Id == itemId);
        if (item is null)
        {
            throw new KeyNotFoundException($"Item {itemId} not found in guest cart.");
        }

        _dbContext.CartItems.Remove(item);
        cart.Items.Remove(item);
        cart.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        var cartDto = MapToCartDto(cart);

        // Update Redis cache and reset TTL
        await TryWriteToCache(guestSessionToken, cartDto);

        return MapToGuestCartResponse(guestSessionToken, cart);
    }

    public async Task<Cart?> GetCartEntity(Guid guestSessionToken)
    {
        return await _dbContext.Carts
            .Include(c => c.Items)
            .AsSplitQuery()
            .FirstOrDefaultAsync(c => c.GuestSessionToken == guestSessionToken);
    }

    public async Task DeleteGuestCart(Guid guestSessionToken)
    {
        var cart = await _dbContext.Carts
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.GuestSessionToken == guestSessionToken);

        if (cart is not null)
        {
            _dbContext.CartItems.RemoveRange(cart.Items);
            _dbContext.Carts.Remove(cart);
            await _dbContext.SaveChangesAsync();
        }

        // Invalidate Redis cache
        await _redisWrapper.DeleteGuestCartAsync(guestSessionToken);
    }

    #region Cache Operations

    private async Task<CartDto?> TryGetFromCache(Guid guestSessionToken)
    {
        var json = await _redisWrapper.GetGuestCartAsync(guestSessionToken);

        if (json is null)
        {
            return null;
        }

        var cartDto = _cartSerializer.Deserialize(json, guestSessionToken);

        if (cartDto is null)
        {
            // Malformed or unrecognized schema — discard cached entry
            await _redisWrapper.DeleteGuestCartAsync(guestSessionToken);
        }

        return cartDto;
    }

    private async Task TryWriteToCache(Guid guestSessionToken, CartDto cartDto)
    {
        var json = _cartSerializer.Serialize(cartDto);
        await _redisWrapper.SetGuestCartAsync(guestSessionToken, json, GuestCacheTtl);
    }

    #endregion

    #region Mapping

    private CartDto MapToCartDto(Cart cart)
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

    private GuestCartResponse MapToGuestCartResponse(Guid guestSessionToken, Cart cart)
    {
        var items = cart.Items.Select(i => new CartItemDto(
            i.Id,
            i.ProductId,
            i.ProductName,
            new MoneyDto(i.UnitPriceAmount, i.UnitPriceCurrency),
            i.Quantity
        )).ToList();

        var totalPrice = _priceCalculator.CalculateTotal(items);

        return new GuestCartResponse(
            guestSessionToken,
            cart.Id,
            items,
            totalPrice,
            cart.CreatedAt,
            cart.UpdatedAt
        );
    }

    private GuestCartResponse MapCachedToGuestCartResponse(Guid guestSessionToken, CartDto cachedCart)
    {
        return new GuestCartResponse(
            guestSessionToken,
            cachedCart.Id,
            cachedCart.Items,
            cachedCart.TotalPrice,
            cachedCart.CreatedAt,
            cachedCart.UpdatedAt
        );
    }

    #endregion
}

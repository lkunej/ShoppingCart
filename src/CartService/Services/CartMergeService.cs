using CartService.DAL.Data;
using CartService.DAL.Models;
using CartService.Infrastructure;
using CartService.Models.DTOs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shared.Models.DTOs;
using Shared.Models.Exceptions;
using Shared.Models.Interfaces;

namespace CartService.Services;

public class CartMergeService : ICartMergeService
{
    private const int MaxCartItems = 50;
    private const int MaxQuantityPerItem = 9999;

    private readonly CartDbContext _dbContext;
    private readonly IGuestCartService _guestCartService;
    private readonly IInventoryClient _inventoryClient;
    private readonly IPriceCalculator _priceCalculator;
    private readonly ICartRedisWrapper _redisWrapper;
    private readonly ILogger<CartMergeService> _logger;

    public CartMergeService(
        CartDbContext dbContext,
        IGuestCartService guestCartService,
        IInventoryClient inventoryClient,
        IPriceCalculator priceCalculator,
        ICartRedisWrapper redisWrapper,
        ILogger<CartMergeService> logger)
    {
        _dbContext = dbContext;
        _guestCartService = guestCartService;
        _inventoryClient = inventoryClient;
        _priceCalculator = priceCalculator;
        _redisWrapper = redisWrapper;
        _logger = logger;
    }

    public async Task<MergeResult> MergeGuestCart(Guid userId, Guid guestSessionToken)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync();

        // Load the guest cart
        var guestCart = await _guestCartService.GetCartEntity(guestSessionToken);

        // Load or create the authenticated cart
        var authCart = await _dbContext.Carts
            .Include(c => c.Items)
            .SingleOrDefaultAsync(c => c.UserId == userId && c.GuestSessionToken == null);

        if (authCart is null)
        {
            authCart = new Cart
            {
                UserId = userId,
                GuestSessionToken = null,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _dbContext.Carts.Add(authCart);
        }

        // If guest cart doesn't exist or is empty, return the auth cart unchanged
        if (guestCart is null || guestCart.Items.Count == 0)
        {
            var unchangedItems = MapItemsToDto(authCart.Items);
            var unchangedTotal = _priceCalculator.CalculateTotal(unchangedItems);

            return new MergeResult(
                new MergeResponse(
                    authCart.Id,
                    userId,
                    unchangedItems,
                    unchangedTotal,
                    null,
                    false,
                    false),
                true);
        }

        // Snapshot original auth quantities by ProductId before merge
        var originalAuthQuantities = authCart.Items
            .ToDictionary(i => i.ProductId, i => i.Quantity);

        // Snapshot original guest quantities by ProductId
        var originalGuestQuantities = guestCart.Items
            .ToDictionary(i => i.ProductId, i => i.Quantity);

        var adjustments = new List<MergeAdjustment>();
        var cartItemLimitReached = false;

        // Merge guest items into auth cart
        foreach (var guestItem in guestCart.Items)
        {
            if (authCart.Items.Count >= MaxCartItems)
            {
                cartItemLimitReached = true;
                break;
            }

            var existingItem = authCart.Items.FirstOrDefault(i => i.ProductId == guestItem.ProductId);

            if (existingItem is null)
            {
                // Guest-only item: add to auth cart
                var newItem = new CartItem
                {
                    CartId = authCart.Id,
                    ProductId = guestItem.ProductId,
                    ProductName = guestItem.ProductName,
                    UnitPriceAmount = guestItem.UnitPriceAmount,
                    UnitPriceCurrency = guestItem.UnitPriceCurrency,
                    Quantity = guestItem.Quantity,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                authCart.Items.Add(newItem);
            }
            else
            {
                // Conflict: take higher quantity, cap at 9999
                var originalAuthQuantity = existingItem.Quantity;
                var originalGuestQuantity = guestItem.Quantity;
                var mergedQuantity = Math.Min(Math.Max(originalGuestQuantity, originalAuthQuantity), MaxQuantityPerItem);

                // Track adjustment if merged quantity differs from both originals
                if (mergedQuantity != originalGuestQuantity && mergedQuantity != originalAuthQuantity)
                {
                    adjustments.Add(new MergeAdjustment(
                        guestItem.ProductId,
                        guestItem.ProductName,
                        originalGuestQuantity,
                        originalAuthQuantity,
                        mergedQuantity,
                        "conflict_resolution"));
                }

                existingItem.Quantity = mergedQuantity;
                existingItem.UpdatedAt = DateTime.UtcNow;
            }
        }

        // Stock validation pass
        var stockValidationSkipped = false;
        var itemsToRemove = new List<CartItem>();

        foreach (var item in authCart.Items.ToList())
        {
            if (stockValidationSkipped)
                break;

            try
            {
                // CheckAvailability throws InsufficientStockException if requested > available
                await _inventoryClient.CheckAvailability(item.ProductId, item.Quantity);
                // If no exception, stock is sufficient — no adjustment needed
            }
            catch (InsufficientStockException ex)
            {
                var availableQuantity = ex.AvailableQuantity;
                var originalGuestQty = originalGuestQuantities.GetValueOrDefault(item.ProductId, 0);
                var originalAuthQty = originalAuthQuantities.GetValueOrDefault(item.ProductId, 0);

                if (availableQuantity <= 0)
                {
                    // Remove item entirely
                    itemsToRemove.Add(item);

                    adjustments.Add(new MergeAdjustment(
                        item.ProductId,
                        item.ProductName,
                        originalGuestQty,
                        originalAuthQty,
                        0,
                        "stock_limit"));
                }
                else
                {
                    adjustments.Add(new MergeAdjustment(
                        item.ProductId,
                        item.ProductName,
                        originalGuestQty,
                        originalAuthQty,
                        availableQuantity,
                        "stock_limit"));

                    item.Quantity = availableQuantity;
                    item.UpdatedAt = DateTime.UtcNow;
                }
            }
            catch (Exception ex) when (ex is not InsufficientStockException)
            {
                // Inventory service failure: complete merge without stock validation
                _logger.LogWarning(ex,
                    "Inventory service failure during merge for user {UserId}. Completing merge without stock validation.",
                    userId);
                stockValidationSkipped = true;

                // Per requirement 6.4: omit stock-related adjustments when stock validation is skipped
                adjustments.RemoveAll(a => a.Reason == "stock_limit");
                // Don't remove items that were marked for removal due to stock
                itemsToRemove.Clear();
            }
        }

        // Remove items with 0 stock
        foreach (var itemToRemove in itemsToRemove)
        {
            authCart.Items.Remove(itemToRemove);
            _dbContext.CartItems.Remove(itemToRemove);
        }

        // Update auth cart timestamp
        authCart.UpdatedAt = DateTime.UtcNow;

        // Persist merged cart
        await _dbContext.SaveChangesAsync();

        // Delete guest cart and invalidate session token
        await _guestCartService.DeleteGuestCart(guestSessionToken);

        // Commit the transaction — all DB changes are atomic
        await transaction.CommitAsync();

        // Invalidate auth cart Redis cache so next read gets fresh data
        await _redisWrapper.DeleteCartAsync(userId);

        // Build response
        var mergedItems = MapItemsToDto(authCart.Items);
        var totalPrice = _priceCalculator.CalculateTotal(mergedItems);

        var response = new MergeResponse(
            authCart.Id,
            userId,
            mergedItems,
            totalPrice,
            adjustments.Count > 0 ? adjustments : null,
            stockValidationSkipped,
            cartItemLimitReached);

        return new MergeResult(response, true);
    }

    private static List<CartItemDto> MapItemsToDto(ICollection<CartItem> items)
    {
        return items.Select(i => new CartItemDto(
            i.Id,
            i.ProductId,
            i.ProductName,
            new MoneyDto(i.UnitPriceAmount, i.UnitPriceCurrency),
            i.Quantity
        )).ToList();
    }
}

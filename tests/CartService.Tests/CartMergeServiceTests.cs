using CartService.DAL.Data;
using CartService.DAL.Models;
using CartService.Infrastructure;
using CartService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shared.Models.DTOs;
using Shared.Models.Exceptions;
using Shared.Models.Interfaces;

namespace CartService.Tests;

public class CartMergeServiceTests : IDisposable
{
    private readonly CartDbContext _dbContext;
    private readonly IGuestCartService _guestCartService;
    private readonly IInventoryClient _inventoryClient;
    private readonly IPriceCalculator _priceCalculator;
    private readonly ICartRedisWrapper _redisWrapper;
    private readonly CartMergeService _sut;

    public CartMergeServiceTests()
    {
        var options = new DbContextOptionsBuilder<CartDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _dbContext = new CartDbContext(options);
        _guestCartService = Substitute.For<IGuestCartService>();
        _inventoryClient = Substitute.For<IInventoryClient>();
        _priceCalculator = Substitute.For<IPriceCalculator>();
        _redisWrapper = Substitute.For<ICartRedisWrapper>();

        _priceCalculator.CalculateTotal(Arg.Any<IEnumerable<CartItemDto>>())
            .Returns(new MoneyDto(0, "EUR"));

        _inventoryClient.CheckAvailability(Arg.Any<Guid>(), Arg.Any<int>())
            .Returns(callInfo => callInfo.ArgAt<int>(1)); // stock is always sufficient

        _sut = new CartMergeService(
            _dbContext,
            _guestCartService,
            _inventoryClient,
            _priceCalculator,
            _redisWrapper,
            Substitute.For<ILogger<CartMergeService>>());
    }

    [Fact]
    public async Task MergeGuestCart_ConflictingItem_TakesHigherQuantity()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var guestToken = Guid.NewGuid();

        // Auth cart with 6x product
        var authCart = new Cart
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            GuestSessionToken = null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Items = new List<CartItem>
            {
                new()
                {
                    ProductId = productId,
                    ProductName = "Widget",
                    UnitPriceAmount = 1000,
                    UnitPriceCurrency = "EUR",
                    Quantity = 6,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                }
            }
        };
        authCart.Items.First().CartId = authCart.Id;
        _dbContext.Carts.Add(authCart);
        await _dbContext.SaveChangesAsync();

        // Guest cart with 5x same product
        var guestCart = new Cart
        {
            UserId = Guid.Empty,
            GuestSessionToken = guestToken,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Items = new List<CartItem>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    ProductId = productId,
                    ProductName = "Widget",
                    UnitPriceAmount = 1000,
                    UnitPriceCurrency = "EUR",
                    Quantity = 5,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                }
            }
        };
        guestCart.Items.First().CartId = guestCart.Id;

        _guestCartService.GetCartEntity(guestToken).Returns(guestCart);

        // Act
        var result = await _sut.MergeGuestCart(userId, guestToken);

        // Assert — max(5, 6) = 6
        Assert.True(result.Success);
        var mergedItem = result.Response.Items.Single();
        Assert.Equal(6, mergedItem.Quantity);
    }

    [Fact]
    public async Task MergeGuestCart_GuestHasHigherQuantity_TakesGuestQuantity()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var guestToken = Guid.NewGuid();

        var authCart = new Cart
        {
            UserId = userId,
            GuestSessionToken = null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Items = new List<CartItem>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    ProductId = productId,
                    ProductName = "Widget",
                    UnitPriceAmount = 1000,
                    UnitPriceCurrency = "EUR",
                    Quantity = 3,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                }
            }
        };
        authCart.Items.First().CartId = authCart.Id;
        _dbContext.Carts.Add(authCart);
        await _dbContext.SaveChangesAsync();

        var guestCart = new Cart
        {
            Id = Guid.NewGuid(),
            UserId = Guid.Empty,
            GuestSessionToken = guestToken,
            Items = new List<CartItem>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    ProductId = productId,
                    ProductName = "Widget",
                    UnitPriceAmount = 1000,
                    UnitPriceCurrency = "EUR",
                    Quantity = 10,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                }
            }
        };
        guestCart.Items.First().CartId = guestCart.Id;

        _guestCartService.GetCartEntity(guestToken).Returns(guestCart);

        // Act
        var result = await _sut.MergeGuestCart(userId, guestToken);

        // Assert — max(10, 3) = 10
        var mergedItem = result.Response.Items.Single();
        Assert.Equal(10, mergedItem.Quantity);
    }

    [Fact]
    public async Task MergeGuestCart_StockInsufficient_CapsQuantityToAvailable()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var guestToken = Guid.NewGuid();

        var authCart = new Cart
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            GuestSessionToken = null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Items = new List<CartItem>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    ProductId = productId,
                    ProductName = "Widget",
                    UnitPriceAmount = 1000,
                    UnitPriceCurrency = "EUR",
                    Quantity = 6,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                }
            }
        };
        authCart.Items.First().CartId = authCart.Id;
        _dbContext.Carts.Add(authCart);
        await _dbContext.SaveChangesAsync();

        var guestCart = new Cart
        {
            Id = Guid.NewGuid(),
            UserId = Guid.Empty,
            GuestSessionToken = guestToken,
            Items = new List<CartItem>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    ProductId = productId,
                    ProductName = "Widget",
                    UnitPriceAmount = 1000,
                    UnitPriceCurrency = "EUR",
                    Quantity = 8,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                }
            }
        };
        guestCart.Items.First().CartId = guestCart.Id;

        _guestCartService.GetCartEntity(guestToken).Returns(guestCart);

        // Stock only has 4 available — throws InsufficientStockException during validation pass
        _inventoryClient.CheckAvailability(productId, Arg.Any<int>())
            .Throws(new InsufficientStockException(productId, 8, 4));

        // Act
        var result = await _sut.MergeGuestCart(userId, guestToken);

        // Assert — capped to available stock (4), adjustment recorded
        var mergedItem = result.Response.Items.Single();
        Assert.Equal(4, mergedItem.Quantity);
        Assert.NotNull(result.Response.Adjustments);
        var adj = result.Response.Adjustments.Single();
        Assert.Equal("stock_limit", adj.Reason);
        Assert.Equal(4, adj.MergedQuantity);
    }

    [Fact]
    public async Task MergeGuestCart_EmptyGuestCart_ReturnsAuthCartUnchanged()
    {
        // Arrange — corner case: guest cart exists but is empty
        var userId = Guid.NewGuid();
        var guestToken = Guid.NewGuid();

        var authCart = new Cart
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            GuestSessionToken = null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Items = new List<CartItem>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    ProductId = Guid.NewGuid(),
                    ProductName = "Existing Item",
                    UnitPriceAmount = 500,
                    UnitPriceCurrency = "EUR",
                    Quantity = 2,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                }
            }
        };
        authCart.Items.First().CartId = authCart.Id;
        _dbContext.Carts.Add(authCart);
        await _dbContext.SaveChangesAsync();

        // Guest cart with zero items
        var emptyGuestCart = new Cart
        {
            Id = Guid.NewGuid(),
            UserId = Guid.Empty,
            GuestSessionToken = guestToken,
            Items = new List<CartItem>()
        };

        _guestCartService.GetCartEntity(guestToken).Returns(emptyGuestCart);

        // Act
        var result = await _sut.MergeGuestCart(userId, guestToken);

        // Assert — auth cart returned as-is, no adjustments, guest cart NOT deleted
        Assert.True(result.Success);
        Assert.Single(result.Response.Items);
        Assert.Null(result.Response.Adjustments);
        Assert.False(result.Response.StockValidationSkipped);
        await _guestCartService.DidNotReceive().DeleteGuestCart(Arg.Any<Guid>());
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }
}

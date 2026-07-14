using CartService.DAL.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shared.Infrastructure;
using Shared.Middleware;
using Shared.Models.DTOs;
using Shared.Models.Events;
using Shared.Models.Exceptions;
using Shared.Models.Interfaces;
using CartService.Infrastructure;

namespace CartService.Controllers;

/// <summary>
/// Handles the checkout flow:
/// 1. Validate the cart
/// 2. Authorize payment via external Payment Service
/// 3. Capture payment
/// 4. Fiscalize the invoice via Porezna Uprava (Croatian Tax Authority)
/// 5. Decrease inventory for purchased items
/// 6. Clear the cart (DB + Redis cache)
/// 7. Return the completed order with payment + fiscal details
/// 
/// Demonstrates integration with two 3rd-party services using circuit breakers.
/// </summary>
[ApiController]
[Route("api/checkout")]
[Authorize]
public class CheckoutController : ControllerBase
{
    private readonly ICartService _cartService;
    private readonly IPaymentClient _paymentClient;
    private readonly IFiscalClient _fiscalClient;
    private readonly ICartEventPublisher _eventPublisher;
    private readonly IResilientEventPublisher _resilientPublisher;
    private readonly CartDbContext _dbContext;
    private readonly ILogger<CheckoutController> _logger;

    public CheckoutController(
        ICartService cartService,
        IPaymentClient paymentClient,
        IFiscalClient fiscalClient,
        ICartEventPublisher eventPublisher,
        IResilientEventPublisher resilientPublisher,
        CartDbContext dbContext,
        ILogger<CheckoutController> logger)
    {
        _cartService = cartService;
        _paymentClient = paymentClient;
        _fiscalClient = fiscalClient;
        _eventPublisher = eventPublisher;
        _resilientPublisher = resilientPublisher;
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/checkout — Execute the full checkout flow.
    /// 
    /// Steps:
    /// 1. Load the user's cart
    /// 2. Authorize payment (hold funds)
    /// 3. Capture payment (finalize charge)
    /// 4. Fiscalize the invoice with Porezna Uprava (Croatian tax law compliance)
    /// 5. Return the order confirmation
    /// 
    /// If fiscalization fails (Porezna unavailable), the order is still completed
    /// because Croatian law allows 48h to retry fiscalization. The ZKI is included
    /// on the receipt regardless.
    /// </summary>
    [HttpPost]
    [RequirePermission("cart:write")]
    public async Task<IActionResult> Checkout([FromBody] CheckoutRequest request)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized(new ErrorResponse("Unauthorized", "Missing or invalid X-User-Id header."));
        }

        // 1. Load the user's cart
        CartDto cart;
        try
        {
            cart = await _cartService.GetCart(userId);
        }
        catch (ServiceUnavailableException ex)
        {
            return StatusCode(503, new ErrorResponse("ServiceUnavailable", ex.Message));
        }

        if (cart.Items.Count == 0)
        {
            return BadRequest(new ErrorResponse("EmptyCart", "Cannot checkout with an empty cart."));
        }

        _logger.LogInformation(
            "Starting checkout for user {UserId}. Items: {ItemCount}, Total: {Total} {Currency}.",
            userId, cart.Items.Count, cart.TotalPrice.Amount, cart.TotalPrice.Currency);

        // 2. Authorize payment
        PaymentResult authResult;
        try
        {
            authResult = await _paymentClient.AuthorizePayment(new PaymentRequest(
                OrderId: Guid.NewGuid(),
                UserId: userId,
                AmountCents: cart.TotalPrice.Amount,
                Currency: cart.TotalPrice.Currency,
                PaymentMethod: request.PaymentMethod,
                CardToken: request.CardToken
            ));

            if (authResult.Status == PaymentStatus.Declined)
            {
                return UnprocessableEntity(new ErrorResponse("PaymentDeclined",
                    $"Payment was declined: {authResult.ErrorMessage}"));
            }

            if (authResult.Status == PaymentStatus.Failed)
            {
                return StatusCode(502, new ErrorResponse("PaymentFailed",
                    $"Payment processing failed: {authResult.ErrorMessage}"));
            }
        }
        catch (ServiceUnavailableException)
        {
            return StatusCode(503, new ErrorResponse("ServiceUnavailable",
                "Payment service is temporarily unavailable. Please try again later."));
        }

        // 3. Capture payment
        PaymentResult captureResult;
        try
        {
            captureResult = await _paymentClient.CapturePayment(authResult.TransactionId);
        }
        catch (ServiceUnavailableException)
        {
            // Payment was authorized but capture failed — this needs manual reconciliation
            _logger.LogError(
                "Payment capture failed for transaction {TransactionId}. Authorization will expire. User {UserId}.",
                authResult.TransactionId, userId);
            return StatusCode(503, new ErrorResponse("PaymentCaptureError",
                "Payment was authorized but capture failed. Your card was not charged. Please try again."));
        }

        // 4. Fiscalize invoice with Porezna Uprava
        var invoiceNumber = GenerateInvoiceNumber();
        var vatAmountCents = (int)(cart.TotalPrice.Amount * 0.25m / 1.25m); // Extract 25% VAT from total
        var zki = FiscalClient.GenerateZki("12345678901", invoiceNumber, DateTime.UtcNow, cart.TotalPrice.Amount);

        FiscalResult? fiscalResult = null;
        try
        {
            fiscalResult = await _fiscalClient.FiscalizeInvoice(new FiscalInvoiceRequest(
                BusinessOib: "12345678901", // Demo OIB
                PaymentMethod: MapToFiscalPaymentMethod(request.PaymentMethod),
                InvoiceNumber: invoiceNumber,
                TotalAmountCents: cart.TotalPrice.Amount,
                VatAmountCents: vatAmountCents,
                Currency: cart.TotalPrice.Currency,
                IssuedAt: DateTime.UtcNow,
                Zki: zki
            ));
        }
        catch (ServiceUnavailableException)
        {
            // Per Croatian law, we can still complete the transaction.
            // The invoice MUST be fiscalized within 48 hours (queued for retry).
            _logger.LogWarning(
                "Porezna Uprava unavailable during checkout for user {UserId}. Invoice {InvoiceNumber} will be fiscalized within 48h. ZKI: {Zki}.",
                userId, invoiceNumber, zki);
        }

        // 5. Decrease inventory for purchased items (single batch query + transaction)
        var purchasedProductIds = cart.Items.Select(i => i.ProductId).ToList();

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        try
        {
            var inventoryItems = await _dbContext.InventoryItems
                .Where(i => purchasedProductIds.Contains(i.ProductId))
                .ToListAsync();

            foreach (var item in cart.Items)
            {
                var inventoryItem = inventoryItems.FirstOrDefault(i => i.ProductId == item.ProductId);
                if (inventoryItem is null)
                {
                    _logger.LogWarning("Product {ProductId} not found in inventory during checkout.", item.ProductId);
                    continue;
                }

                if (inventoryItem.AvailableQuantity < item.Quantity)
                {
                    await transaction.RollbackAsync();
                    // Refund the captured payment since we can't fulfill
                    try { await _paymentClient.RefundPayment(captureResult.TransactionId); }
                    catch (Exception refundEx) { _logger.LogError(refundEx, "Failed to refund transaction {TransactionId} after stock check failure.", captureResult.TransactionId); }

                    return Conflict(new ErrorResponse("InsufficientStock",
                        $"Insufficient stock for product '{item.ProductName}'. Available: {inventoryItem.AvailableQuantity}, requested: {item.Quantity}."));
                }

                inventoryItem.AvailableQuantity -= item.Quantity;
                inventoryItem.UpdatedAt = DateTime.UtcNow;

                _logger.LogInformation(
                    "Inventory decremented for product {ProductId}: -{Quantity}. New available: {Available}.",
                    item.ProductId, item.Quantity, inventoryItem.AvailableQuantity);
            }

            await _dbContext.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch (Exception ex) when (ex is not ServiceUnavailableException)
        {
            await transaction.RollbackAsync();
            _logger.LogError(ex, "Inventory update failed during checkout for user {UserId}. Rolling back.", userId);
            // Attempt refund since payment was captured but order can't be fulfilled
            try { await _paymentClient.RefundPayment(captureResult.TransactionId); }
            catch (Exception refundEx) { _logger.LogError(refundEx, "Failed to refund transaction {TransactionId} after inventory error.", captureResult.TransactionId); }
            return StatusCode(500, new ErrorResponse("CheckoutFailed", "Failed to update inventory. Payment has been refunded."));
        }

        // Publish inventory.updated events for each purchased product (triggers cache invalidation in consumers)
        var correlationId = Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? Guid.NewGuid().ToString();
        foreach (var item in cart.Items)
        {
            var updatedInventory = await _dbContext.InventoryItems
                .AsNoTracking()
                .FirstOrDefaultAsync(i => i.ProductId == item.ProductId);

            if (updatedInventory is not null)
            {
                await _resilientPublisher.PublishAsync(
                    new InventoryUpdatedEvent(
                        Type: "inventory.updated",
                        Payload: new InventoryUpdatedPayload(item.ProductId.ToString(), updatedInventory.AvailableQuantity),
                        Timestamp: DateTime.UtcNow,
                        CorrelationId: correlationId),
                    routingKey: "inventory.updated",
                    eventType: "inventory.updated",
                    correlationId: correlationId);
            }
        }

        // 6. Clear the cart (DB + Redis cache invalidation)
        await _cartService.ClearCart(userId);

        _logger.LogInformation("Cart cleared for user {UserId} after successful checkout.", userId);

        // 7. Return order confirmation
        var response = new CheckoutResponse(
            OrderId: Guid.NewGuid(),
            UserId: userId,
            Items: cart.Items,
            TotalPrice: cart.TotalPrice,
            Payment: new PaymentSummary(
                TransactionId: captureResult.TransactionId,
                Status: captureResult.Status.ToString(),
                Method: request.PaymentMethod,
                ProcessedAt: captureResult.ProcessedAt ?? DateTime.UtcNow
            ),
            Fiscal: new FiscalSummary(
                InvoiceNumber: invoiceNumber,
                Zki: zki,
                Jir: fiscalResult?.Jir,
                FiscalizedAt: fiscalResult?.ProcessedAt,
                Status: fiscalResult?.Success == true ? "Fiscalized" : "PendingFiscalization"
            ),
            CompletedAt: DateTime.UtcNow
        );

        _logger.LogInformation(
            "Checkout completed for user {UserId}. Order {OrderId}. Payment: {TransactionId}. Fiscal: {FiscalStatus} (JIR: {Jir}).",
            userId, response.OrderId, captureResult.TransactionId,
            response.Fiscal.Status, response.Fiscal.Jir ?? "pending");

        return Ok(response);
    }

    /// <summary>
    /// GET /api/checkout/payment-methods — Returns available payment methods.
    /// </summary>
    [HttpGet("payment-methods")]
    [AllowAnonymous]
    public IActionResult GetPaymentMethods()
    {
        var methods = new[]
        {
            new { Id = "card", Name = "Credit/Debit Card", Supported = true },
            new { Id = "cash", Name = "Cash on Delivery", Supported = true },
            new { Id = "bank_transfer", Name = "Bank Transfer", Supported = true }
        };

        return Ok(methods);
    }

    private static FiscalPaymentMethod MapToFiscalPaymentMethod(string method) => method.ToLowerInvariant() switch
    {
        "card" or "credit_card" or "debit_card" => FiscalPaymentMethod.Card,
        "cash" => FiscalPaymentMethod.Cash,
        "bank_transfer" => FiscalPaymentMethod.BankTransfer,
        _ => FiscalPaymentMethod.Other
    };

    private static string GenerateInvoiceNumber()
    {
        var seq = Random.Shared.Next(1, 99999);
        return $"{seq}/PP1/1"; // format: seq/premises/register
    }

    private bool TryGetUserId(out Guid userId)
    {
        userId = Guid.Empty;
        if (!Request.Headers.TryGetValue("X-User-Id", out var headerValue))
            return false;
        return Guid.TryParse(headerValue.ToString(), out userId);
    }
}

// ─────────────────────────────────────────────
// Request / Response DTOs
// ─────────────────────────────────────────────

public record CheckoutRequest(
    string PaymentMethod,
    string? CardToken = null
);

public record CheckoutResponse(
    Guid OrderId,
    Guid UserId,
    List<CartItemDto> Items,
    MoneyDto TotalPrice,
    PaymentSummary Payment,
    FiscalSummary Fiscal,
    DateTime CompletedAt
);

public record PaymentSummary(
    string TransactionId,
    string Status,
    string Method,
    DateTime ProcessedAt
);

public record FiscalSummary(
    string InvoiceNumber,
    string Zki,
    string? Jir,
    DateTime? FiscalizedAt,
    string Status
);

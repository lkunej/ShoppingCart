namespace Shared.Models.Interfaces;

/// <summary>
/// Client interface for the Croatian Tax Authority (Porezna Uprava) fiscal service.
/// Implements the mandatory fiscalization protocol (fiskalizacija) for invoice reporting.
/// 
/// In Croatia, every cash register transaction must be reported to the tax authority
/// in real-time, receiving a unique fiscal identifier (JIR) back.
/// </summary>
public interface IFiscalClient
{
    /// <summary>
    /// Sends an invoice to Porezna Uprava for fiscalization.
    /// Returns the JIR (Jedinstveni Identifikator Računa) — a unique invoice identifier
    /// assigned by the tax authority.
    /// </summary>
    Task<FiscalResult> FiscalizeInvoice(FiscalInvoiceRequest request);

    /// <summary>
    /// Sends a storno (cancellation) invoice to Porezna Uprava.
    /// </summary>
    Task<FiscalResult> CancelInvoice(string originalJir, FiscalInvoiceRequest stornoRequest);

    /// <summary>
    /// Checks connectivity/health of the Porezna Uprava service.
    /// Used by circuit breaker probes.
    /// </summary>
    Task<bool> IsServiceAvailable();
}

/// <summary>
/// Request model for fiscalizing an invoice with Porezna Uprava.
/// Maps to the XML schema required by the Croatian fiskalizacija protocol.
/// </summary>
public record FiscalInvoiceRequest(
    /// <summary>OIB (Personal Identification Number) of the business entity.</summary>
    string BusinessOib,

    /// <summary>Marks whether it's a cash (G) or card (K) transaction.</summary>
    FiscalPaymentMethod PaymentMethod,

    /// <summary>Invoice number in format: numeričkibroj/oznakaPP/oznakaNO</summary>
    string InvoiceNumber,

    /// <summary>Total amount including VAT, in cents.</summary>
    int TotalAmountCents,

    /// <summary>VAT amount in cents (25% standard rate in Croatia).</summary>
    int VatAmountCents,

    /// <summary>ISO 4217 currency code.</summary>
    string Currency,

    /// <summary>Timestamp of the transaction.</summary>
    DateTime IssuedAt,

    /// <summary>The ZKI (Zaštitni Kod Izdavatelja) — protective code of the issuer.</summary>
    string Zki
);

public enum FiscalPaymentMethod
{
    /// <summary>Gotovina (Cash)</summary>
    Cash,
    /// <summary>Kartica (Card)</summary>
    Card,
    /// <summary>Transakcijski račun (Bank transfer)</summary>
    BankTransfer,
    /// <summary>Ostalo (Other)</summary>
    Other
}

/// <summary>
/// Result from the Porezna Uprava fiscalization service.
/// </summary>
public record FiscalResult(
    /// <summary>Whether the fiscalization was successful.</summary>
    bool Success,

    /// <summary>JIR — Unique invoice identifier assigned by Porezna Uprava (null on failure).</summary>
    string? Jir,

    /// <summary>Error code returned by the tax authority (null on success).</summary>
    string? ErrorCode,

    /// <summary>Human-readable error message (null on success).</summary>
    string? ErrorMessage,

    /// <summary>Timestamp when the response was received.</summary>
    DateTime ProcessedAt
);

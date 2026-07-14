using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Shared.Models.Exceptions;
using Shared.Models.Interfaces;

namespace CartService.Infrastructure;

/// <summary>
/// HTTP/SOAP client for Croatian Tax Authority (Porezna Uprava) fiscalization service.
/// 
/// Implements the CIS (Centralni Informacijski Sustav) protocol for invoice fiscalization.
/// In production this sends XML-signed SOAP messages to the Porezna Uprava endpoint.
/// 
/// Resilience features:
/// - Circuit breaker (opens after 5 failures in 60s)
/// - Retry with exponential backoff (3 attempts)
/// - Request timeout (10s — Porezna allows up to 2s response, but network can be slow)
/// 
/// Croatian law requires:
/// - If the service is unavailable, the business MUST retry within 48 hours
/// - A ZKI (protective code) is generated locally and printed on the receipt immediately
/// - The JIR is obtained asynchronously and can be provided to the customer later
/// </summary>
public class FiscalClient : IFiscalClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<FiscalClient> _logger;
    private readonly FiscalServiceOptions _options;
    private readonly ResiliencePipeline _resiliencePipeline;

    public FiscalClient(
        HttpClient httpClient,
        ILogger<FiscalClient> logger,
        IOptions<FiscalServiceOptions> options)
    {
        _httpClient = httpClient;
        _logger = logger;
        _options = options.Value;

        _resiliencePipeline = new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(60),
                MinimumThroughput = 5,
                BreakDuration = TimeSpan.FromSeconds(30),
                OnOpened = args =>
                {
                    _logger.LogWarning(
                        "Porezna Uprava circuit breaker OPENED. Fiscalization requests will be queued for retry. Break duration: {Duration}s.",
                        args.BreakDuration.TotalSeconds);
                    return ValueTask.CompletedTask;
                },
                OnClosed = args =>
                {
                    _logger.LogInformation("Porezna Uprava circuit breaker CLOSED. Fiscalization resumed.");
                    return ValueTask.CompletedTask;
                }
            })
            .AddRetry(new Polly.Retry.RetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(1),
                BackoffType = DelayBackoffType.Exponential,
                ShouldHandle = new PredicateBuilder().Handle<HttpRequestException>().Handle<TaskCanceledException>(),
                OnRetry = args =>
                {
                    _logger.LogWarning(
                        "Porezna Uprava retry attempt {Attempt} after {Delay}ms.",
                        args.AttemptNumber, args.RetryDelay.TotalMilliseconds);
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(TimeSpan.FromSeconds(10))
            .Build();
    }

    public async Task<FiscalResult> FiscalizeInvoice(FiscalInvoiceRequest request)
    {
        _logger.LogInformation(
            "Fiscalizing invoice {InvoiceNumber} for OIB {Oib}, amount {Amount} {Currency}.",
            request.InvoiceNumber, request.BusinessOib, request.TotalAmountCents, request.Currency);

        try
        {
            var result = await _resiliencePipeline.ExecuteAsync(async ct =>
            {
                // In production: build XML SOAP envelope, sign with X.509 certificate,
                // POST to _options.BaseUrl (e.g., https://cis.porezna-uprava.hr/v1)
                return await SimulateFiscalizationCall(request, ct);
            });

            if (result.Success)
            {
                _logger.LogInformation(
                    "Invoice {InvoiceNumber} fiscalized successfully. JIR: {Jir}.",
                    request.InvoiceNumber, result.Jir);
            }
            else
            {
                _logger.LogWarning(
                    "Invoice {InvoiceNumber} fiscalization failed. Error: {ErrorCode} - {ErrorMessage}.",
                    request.InvoiceNumber, result.ErrorCode, result.ErrorMessage);
            }

            return result;
        }
        catch (BrokenCircuitException)
        {
            _logger.LogError(
                "Porezna Uprava circuit breaker is open. Invoice {InvoiceNumber} will be queued for later fiscalization (48h window per Croatian law).",
                request.InvoiceNumber);

            // Per Croatian law, we can still issue the receipt with ZKI.
            // The JIR must be obtained within 48 hours via retry.
            throw new ServiceUnavailableException("PoreznaUprava");
        }
    }

    public async Task<FiscalResult> CancelInvoice(string originalJir, FiscalInvoiceRequest stornoRequest)
    {
        _logger.LogInformation(
            "Cancelling fiscalized invoice with JIR {Jir}. Storno invoice: {InvoiceNumber}.",
            originalJir, stornoRequest.InvoiceNumber);

        try
        {
            var result = await _resiliencePipeline.ExecuteAsync(async ct =>
            {
                return await SimulateStornoCal(originalJir, stornoRequest, ct);
            });

            return result;
        }
        catch (BrokenCircuitException)
        {
            _logger.LogError("Porezna Uprava circuit breaker is open. Storno for JIR {Jir} will be retried.", originalJir);
            throw new ServiceUnavailableException("PoreznaUprava");
        }
    }

    public async Task<bool> IsServiceAvailable()
    {
        try
        {
            // In production: send a ping/echo request to the CIS endpoint
            await Task.Delay(50);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Porezna Uprava health check failed.");
            return false;
        }
    }

    /// <summary>
    /// Generates the ZKI (Zaštitni Kod Izdavatelja) — a locally computed protective code
    /// that is printed on the receipt even if Porezna Uprava is unreachable.
    /// In production: HMAC-SHA256 of invoice data signed with the business certificate.
    /// </summary>
    public static string GenerateZki(string oib, string invoiceNumber, DateTime issuedAt, int totalAmountCents)
    {
        var data = $"{oib}{issuedAt:dd.MM.yyyy HH:mm:ss}{invoiceNumber}{totalAmountCents}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    #region Simulated External Calls (replace with real SOAP/XML in production)

    private async Task<FiscalResult> SimulateFiscalizationCall(FiscalInvoiceRequest request, CancellationToken ct)
    {
        // Simulate Porezna Uprava response time (typically 500ms-2s)
        await Task.Delay(Random.Shared.Next(200, 800), ct);

        // Simulate a successful fiscalization with a JIR
        var jir = Guid.NewGuid().ToString(); // In reality: UUID returned by Porezna Uprava

        return new FiscalResult(
            Success: true,
            Jir: jir,
            ErrorCode: null,
            ErrorMessage: null,
            ProcessedAt: DateTime.UtcNow
        );
    }

    private async Task<FiscalResult> SimulateStornoCal(string originalJir, FiscalInvoiceRequest stornoRequest, CancellationToken ct)
    {
        await Task.Delay(Random.Shared.Next(200, 600), ct);

        return new FiscalResult(
            Success: true,
            Jir: Guid.NewGuid().ToString(),
            ErrorCode: null,
            ErrorMessage: null,
            ProcessedAt: DateTime.UtcNow
        );
    }

    #endregion
}

/// <summary>
/// Configuration for the Croatian Tax Authority (Porezna Uprava) fiscal service.
/// </summary>
public class FiscalServiceOptions
{
    public const string SectionName = "FiscalService";

    /// <summary>CIS endpoint. Production: https://cis.porezna-uprava.hr/v1, Test: https://cistest.apis-it.hr:8449/FiskalizacijaServiceTest</summary>
    public string BaseUrl { get; set; } = "https://cistest.apis-it.hr:8449/FiskalizacijaServiceTest";

    /// <summary>Path to the X.509 certificate (.pfx) used for signing SOAP messages.</summary>
    public string CertificatePath { get; set; } = string.Empty;

    /// <summary>Password for the .pfx certificate.</summary>
    public string CertificatePassword { get; set; } = string.Empty;

    /// <summary>OIB of the business entity.</summary>
    public string BusinessOib { get; set; } = string.Empty;

    /// <summary>Business premises identifier (oznaka poslovnog prostora).</summary>
    public string PremisesId { get; set; } = "1";

    /// <summary>Cash register identifier (oznaka naplatnog uređaja).</summary>
    public string RegisterId { get; set; } = "1";

    /// <summary>Request timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 10;
}

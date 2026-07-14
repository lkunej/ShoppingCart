using System.Security.Cryptography;
using System.Text.Json;
using CartService.DAL.Data;
using CartService.Infrastructure;
using CartService.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Prometheus;
using RabbitMQ.Client;
using Shared.Infrastructure;
using Shared.Middleware;
using Shared.Models.Interfaces;
using Shared.Services;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// ───────────────────────────────────────────────────────
// Structured JSON Logging
// ───────────────────────────────────────────────────────
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.JsonWriterOptions = new JsonWriterOptions { Indented = false };
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
    options.UseUtcTimestamp = true;
});

// ───────────────────────────────────────────────────────
// Controllers + Swagger
// ───────────────────────────────────────────────────────
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "Paste your JWT access token from AuthService (without the 'Bearer ' prefix)."
    });

    options.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

// ───────────────────────────────────────────────────────
// EF Core with Npgsql (PostgreSQL)
// ───────────────────────────────────────────────────────
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=localhost;Database=cart_db;Username=postgres;Password=postgres";

builder.Services.AddDbContext<CartDbContext>(options =>
    options.UseNpgsql(connectionString));

// ───────────────────────────────────────────────────────
// Redis (StackExchange.Redis)
// ───────────────────────────────────────────────────────
var redisConnectionString = builder.Configuration["Redis:ConnectionString"] ?? "localhost:6379";
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    var config = ConfigurationOptions.Parse(redisConnectionString);
    config.AbortOnConnectFail = false;
    return ConnectionMultiplexer.Connect(config);
});

// ───────────────────────────────────────────────────────
// RabbitMQ (IConnectionFactory)
// ───────────────────────────────────────────────────────
builder.Services.AddSingleton<IConnectionFactory>(sp =>
{
    var rabbitHost = builder.Configuration["RabbitMQ:Host"] ?? "localhost";
    var rabbitPort = int.Parse(builder.Configuration["RabbitMQ:Port"] ?? "5672");
    var rabbitUser = builder.Configuration["RabbitMQ:Username"] ?? "guest";
    var rabbitPass = builder.Configuration["RabbitMQ:Password"] ?? "guest";

    return new ConnectionFactory
    {
        HostName = rabbitHost,
        Port = rabbitPort,
        UserName = rabbitUser,
        Password = rabbitPass,
        AutomaticRecoveryEnabled = true,
        NetworkRecoveryInterval = TimeSpan.FromSeconds(10)
    };
});

// ───────────────────────────────────────────────────────
// JWT Authentication (RS256) — verify only (no token issuance)
// ───────────────────────────────────────────────────────
var publicKeyPem = builder.Configuration["Jwt:PublicKey"] ?? string.Empty;

// Support loading the public key from a file path
var publicKeyPath = builder.Configuration["Jwt:PublicKeyPath"] ?? string.Empty;
if (string.IsNullOrWhiteSpace(publicKeyPem) && !string.IsNullOrWhiteSpace(publicKeyPath))
{
    if (File.Exists(publicKeyPath))
    {
        publicKeyPem = File.ReadAllText(publicKeyPath).Trim();
        Console.WriteLine($"✅ Loaded JWT public key from: {publicKeyPath}");
    }
    else
    {
        Console.WriteLine($"⚠️  Jwt:PublicKeyPath '{publicKeyPath}' does not exist.");
    }
}

if (string.IsNullOrWhiteSpace(publicKeyPem))
{
    // Auto-generate ephemeral RSA key pair for development
    var devRsa = RSA.Create(2048);
    publicKeyPem = devRsa.ExportSubjectPublicKeyInfoPem();
    builder.Configuration["Jwt:PublicKey"] = publicKeyPem;

    Console.WriteLine("⚠️  No JWT public key configured — auto-generated ephemeral RSA key for development.");
    Console.WriteLine("   Token verification will not work with tokens from Auth Service. Set Jwt:PublicKey or Jwt:PublicKeyPath.");
}

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    var rsa = RSA.Create();
    rsa.ImportFromPem(publicKeyPem.ToCharArray());
    var rsaKey = new RsaSecurityKey(rsa);

    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = rsaKey,
        ValidateIssuer = false,
        ValidateAudience = false,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.Zero,
        RoleClaimType = "role",
        NameClaimType = "sub"
    };

    options.Events = new JwtBearerEvents
    {
        OnAuthenticationFailed = context =>
        {
            var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
            logger.LogWarning("JWT authentication failed: {Error}", context.Exception.Message);
            return Task.CompletedTask;
        }
    };
});

builder.Services.AddAuthorization();

// ───────────────────────────────────────────────────────
// Service Registrations (DI)
// ───────────────────────────────────────────────────────

// Scoped services (depend on CartDbContext which is scoped)
builder.Services.AddScoped<ICartService, CartService.Services.CartService>();
builder.Services.AddScoped<IInventoryClient, InventoryClient>();

// Singleton services (stateless or manage their own state)
builder.Services.AddSingleton<IPriceCalculator, PriceCalculator>();
builder.Services.AddSingleton<ICartSerializer, CartSerializer>();
builder.Services.AddSingleton<ICartRedisWrapper, CartRedisWrapper>();
builder.Services.AddSingleton<IResilientEventPublisher, ResilientEventPublisher<CartDbContext>>();
builder.Services.AddSingleton<ICartEventPublisher, CartEventPublisher>();
builder.Services.AddSingleton<IRBACService, RBACService>();

// Background services
builder.Services.AddHostedService<InventoryEventConsumer>();

// ───────────────────────────────────────────────────────
// 3rd-Party Service Integrations
// ───────────────────────────────────────────────────────

// Payment Service (external gateway with circuit breaker)
builder.Services.Configure<PaymentServiceOptions>(
    builder.Configuration.GetSection(PaymentServiceOptions.SectionName));
builder.Services.AddHttpClient<IPaymentClient, PaymentClient>(client =>
{
    var baseUrl = builder.Configuration["PaymentService:BaseUrl"] ?? "https://api.payment-provider.example.com";
    client.BaseAddress = new Uri(baseUrl);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    client.Timeout = TimeSpan.FromSeconds(10);
});

// Fiscal Service — Porezna Uprava (Croatian Tax Authority, SOAP/XML with circuit breaker)
builder.Services.Configure<FiscalServiceOptions>(
    builder.Configuration.GetSection(FiscalServiceOptions.SectionName));
builder.Services.AddHttpClient<IFiscalClient, FiscalClient>(client =>
{
    var baseUrl = builder.Configuration["FiscalService:BaseUrl"] ?? "https://cistest.apis-it.hr:8449/FiskalizacijaServiceTest";
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(15);
});

// ───────────────────────────────────────────────────────
// Build the App
// ───────────────────────────────────────────────────────
var app = builder.Build();

// ───────────────────────────────────────────────────────
// Auto-migrate database on startup (Development only)
// ───────────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CartDbContext>();
    await db.Database.MigrateAsync();
}

// ───────────────────────────────────────────────────────
// Middleware Pipeline
// ───────────────────────────────────────────────────────

// Correlation ID propagation
app.Use(async (context, next) =>
{
    const string correlationHeader = "X-Correlation-Id";
    if (!context.Request.Headers.ContainsKey(correlationHeader))
    {
        context.Request.Headers[correlationHeader] = CommonUtilities.GenerateCorrelationId();
    }

    var correlationId = context.Request.Headers[correlationHeader].ToString();
    context.Response.Headers[correlationHeader] = correlationId;

    // Add to logging scope
    using (app.Logger.BeginScope(new Dictionary<string, object>
    {
        ["CorrelationId"] = correlationId,
        ["ServiceName"] = "CartService"
    }))
    {
        await next();
    }
});

// Swagger (development only)
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Prometheus HTTP metrics
app.UseHttpMetrics();

// Authentication → Authorization → Permission-based RBAC
app.UseAuthentication();
app.UseAuthorization();
app.UsePermissionAuthorization();

// Extract user ID from JWT "sub" claim and set X-User-Id header
// This allows the CartController to work both behind a gateway and when called directly with a JWT.
app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true
        && !context.Request.Headers.ContainsKey("X-User-Id"))
    {
        var sub = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                  ?? context.User.FindFirst("sub")?.Value;

        if (!string.IsNullOrEmpty(sub))
        {
            context.Request.Headers["X-User-Id"] = sub;
        }
    }

    await next();
});

// Map controllers
app.MapControllers();

// Prometheus metrics endpoint
app.MapMetrics();

app.Run();

// Make Program class accessible for integration tests
public partial class Program { }

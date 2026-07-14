using System.Security.Cryptography;
using System.Text.Json;
using AuthService.DAL.Data;
using AuthService.Infrastructure;
using Shared.Middleware;
using AuthService.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Prometheus;
using RabbitMQ.Client;
using Shared.Infrastructure;
using Shared.Models.Interfaces;
using StackExchange.Redis;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// ───────────────────────────────────────────────────────
// Structured JSON Logging
// ───────────────────────────────────────────────────────
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.JsonWriterOptions = new System.Text.Json.JsonWriterOptions { Indented = false };
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
// Add Swagger with JWT Bearer Auth
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "My API",
        Version = "v1"
    });

    // Define the BearerAuth scheme
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Enter 'Bearer' [space] and then your valid token.\nExample: Bearer eyJhbGciOiJIUzI1NiIs..."
    });

    // Apply BearerAuth globally
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
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
    ?? "Host=localhost;Database=auth_db;Username=postgres;Password=postgres";

builder.Services.AddDbContext<AuthDbContext>(options =>
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
// JWT Authentication (RS256)
// Auto-generate dev keys if none configured
// ───────────────────────────────────────────────────────
var privateKeyPem = builder.Configuration["Jwt:PrivateKey"] ?? string.Empty;
var publicKeyPem = builder.Configuration["Jwt:PublicKey"] ?? string.Empty;

// Support loading keys from file paths
var privateKeyPath = builder.Configuration["Jwt:PrivateKeyPath"] ?? string.Empty;
var publicKeyPath = builder.Configuration["Jwt:PublicKeyPath"] ?? string.Empty;

if (string.IsNullOrWhiteSpace(privateKeyPem) && !string.IsNullOrWhiteSpace(privateKeyPath) && File.Exists(privateKeyPath))
{
    privateKeyPem = File.ReadAllText(privateKeyPath).Trim();
    Console.WriteLine($"✅ Loaded JWT private key from: {privateKeyPath}");
}

if (string.IsNullOrWhiteSpace(publicKeyPem) && !string.IsNullOrWhiteSpace(publicKeyPath) && File.Exists(publicKeyPath))
{
    publicKeyPem = File.ReadAllText(publicKeyPath).Trim();
    Console.WriteLine($"✅ Loaded JWT public key from: {publicKeyPath}");
}

if (string.IsNullOrWhiteSpace(privateKeyPem) || string.IsNullOrWhiteSpace(publicKeyPem))
{
    // Auto-generate RSA keys for development — no external key generation needed
    var devRsa = RSA.Create(2048);
    privateKeyPem = devRsa.ExportPkcs8PrivateKeyPem();
    publicKeyPem = devRsa.ExportSubjectPublicKeyInfoPem();

    Console.WriteLine("⚠️  No JWT keys configured — auto-generated ephemeral RSA keys for development.");
    Console.WriteLine("   Tokens will not survive app restarts. Set Jwt:PrivateKeyPath/Jwt:PublicKeyPath or Jwt:PrivateKey/Jwt:PublicKey.");
}

// Feed keys into configuration so TokenService picks them up
builder.Configuration["Jwt:PrivateKey"] = privateKeyPem;
builder.Configuration["Jwt:PublicKey"] = publicKeyPem;

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
builder.Services.AddSingleton<ITokenService, TokenService>();
builder.Services.AddSingleton<IPasswordService, PasswordService>();
builder.Services.AddSingleton<IRBACService, RBACService>();
builder.Services.AddSingleton<IRateLimiterService, RateLimiterService>();
builder.Services.AddSingleton<IRedisWrapper, RedisWrapper>();
builder.Services.AddSingleton<IResilientEventPublisher, ResilientEventPublisher<AuthDbContext>>();
builder.Services.AddSingleton<IEventPublisher, EventPublisher>();

// ───────────────────────────────────────────────────────
// Build the App
// ───────────────────────────────────────────────────────
var app = builder.Build();

// ───────────────────────────────────────────────────────
// Auto-migrate database on startup
// ───────────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
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
        ["ServiceName"] = "AuthService"
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

// Map controllers
app.MapControllers();

// Prometheus metrics endpoint
app.MapMetrics();

app.Run();

// Make Program class accessible for integration tests
public partial class Program { }

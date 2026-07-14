using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Shared.Models.DTOs;
using Shared.Models.Interfaces;
using StackExchange.Redis;

namespace AuthService.Services;

public class TokenService : ITokenService
{
    private const int AccessTokenExpirySeconds = 9000;        // 30 seconds (demo — change back to 900 for production)
    private const int RefreshTokenExpirySeconds = 604_800; // 60 seconds (demo — change back to 604_800 for production) // 7 days
    private const string RevocationKeyPrefix = "revoked:";
    private const string TokenFamilyKeyPrefix = "token_family:";
    private const string TokenFamilyMembersPrefix = "token_family_members:";

    private readonly RsaSecurityKey _signingKey;
    private readonly RsaSecurityKey _validationKey;
    private readonly SigningCredentials _signingCredentials;
    private readonly TokenValidationParameters _validationParameters;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<TokenService> _logger;

    public TokenService(
        IConfiguration configuration,
        IConnectionMultiplexer redis,
        ILogger<TokenService> logger)
    {
        _redis = redis;
        _logger = logger;

        // Load RSA private key for signing
        var privateKeyPem = configuration["Jwt:PrivateKey"]
            ?? throw new InvalidOperationException("JWT private key not configured (Jwt:PrivateKey).");

        var rsaPrivate = RSA.Create();
        rsaPrivate.ImportFromPem(privateKeyPem.ToCharArray());
        _signingKey = new RsaSecurityKey(rsaPrivate);
        _signingCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.RsaSha256);

        // Load RSA public key for validation
        var publicKeyPem = configuration["Jwt:PublicKey"]
            ?? throw new InvalidOperationException("JWT public key not configured (Jwt:PublicKey).");

        var rsaPublic = RSA.Create();
        rsaPublic.ImportFromPem(publicKeyPem.ToCharArray());
        _validationKey = new RsaSecurityKey(rsaPublic);

        _validationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = _validationKey,
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        };
    }

    /// <inheritdoc />
    public TokenPairResult IssueTokenPair(string userId, string role, string[] permissions)
    {
        var now = DateTimeOffset.UtcNow;
        var accessJti = Guid.NewGuid().ToString();
        var refreshJti = Guid.NewGuid().ToString();

        var accessToken = CreateToken(userId, role, permissions, accessJti, now, AccessTokenExpirySeconds);
        var refreshToken = CreateToken(userId, role, permissions, refreshJti, now, RefreshTokenExpirySeconds);

        // Store the refresh token's family mapping (refreshJti -> familyId)
        // The family ID is the refresh token's own jti for newly issued pairs
        _ = StoreTokenFamilyAsync(refreshJti, refreshJti, RefreshTokenExpirySeconds);

        return new TokenPairResult(accessToken, refreshToken, AccessTokenExpirySeconds);
    }

    /// <inheritdoc />
    public async Task<JwtTokenPayload> ValidateToken(string token)
    {
        var handler = new JwtSecurityTokenHandler();

        ClaimsPrincipal principal;
        try
        {
            principal = handler.ValidateToken(token, _validationParameters, out _);
        }
        catch (SecurityTokenExpiredException)
        {
            throw new TokenValidationException("TokenExpired", "Token has expired.");
        }
        catch (SecurityTokenException)
        {
            throw new TokenValidationException("InvalidToken", "Token is invalid or tampered.");
        }

        var jti = principal.FindFirstValue(JwtRegisteredClaimNames.Jti)
            ?? throw new TokenValidationException("InvalidToken", "Token missing jti claim.");

        // Check revocation in Redis with fallback
        if (await IsTokenRevoked(jti))
        {
            throw new TokenValidationException("Revoked", "Token has been revoked.");
        }

        return ExtractPayload(principal);
    }

    /// <inheritdoc />
    public async Task<TokenPairResult> RefreshToken(string refreshToken)
    {
        var handler = new JwtSecurityTokenHandler();

        ClaimsPrincipal principal;
        try
        {
            principal = handler.ValidateToken(refreshToken, _validationParameters, out _);
        }
        catch (SecurityTokenExpiredException)
        {
            throw new TokenValidationException("InvalidToken", "Refresh token has expired.");
        }
        catch (SecurityTokenException)
        {
            throw new TokenValidationException("InvalidToken", "Refresh token is invalid.");
        }

        var oldJti = principal.FindFirstValue(JwtRegisteredClaimNames.Jti)
            ?? throw new TokenValidationException("InvalidToken", "Refresh token missing jti claim.");

        // Check if this refresh token was already revoked (reuse detection)
        if (await IsTokenRevoked(oldJti))
        {
            // Reuse detected — revoke the entire token family
            await RevokeTokenFamily(oldJti);
            throw new TokenValidationException("Revoked", "Refresh token reuse detected. All tokens in family revoked.");
        }

        // Revoke the old refresh token (single-use rotation)
        await RevokeToken(oldJti, RefreshTokenExpirySeconds);

        // Extract claims from old token and issue new pair
        var payload = ExtractPayload(principal);
        var newPair = IssueTokenPair(payload.Sub, payload.Role, payload.Permissions);

        // Link the new refresh token to the same family as the old one
        var familyId = await GetTokenFamily(oldJti) ?? oldJti;
        var newRefreshJti = GetJtiFromToken(newPair.RefreshToken);
        if (newRefreshJti != null)
        {
            await UpdateTokenFamily(newRefreshJti, familyId);
        }

        return newPair;
    }

    private string CreateToken(
        string userId,
        string role,
        string[] permissions,
        string jti,
        DateTimeOffset issuedAt,
        int expirySeconds)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId),
            new(JwtRegisteredClaimNames.Jti, jti),
            new(JwtRegisteredClaimNames.Iat, issuedAt.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
            new("role", role)
        };

        // Add permissions as individual claims
        foreach (var permission in permissions)
        {
            claims.Add(new Claim("permissions", permission));
        }

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = issuedAt.UtcDateTime.AddSeconds(expirySeconds),
            SigningCredentials = _signingCredentials
        };

        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateToken(tokenDescriptor);
        return handler.WriteToken(token);
    }

    private async Task<bool> IsTokenRevoked(string jti)
    {
        try
        {
            var db = _redis.GetDatabase();
            return await db.KeyExistsAsync($"{RevocationKeyPrefix}{jti}");
        }
        catch (RedisConnectionException ex)
        {
            // Req 2.7: Fall back to signature+expiry validation when Redis is unavailable
            _logger.LogWarning(ex, "Redis unavailable during revocation check for jti {Jti}. Falling back to signature+expiry validation.", jti);
            return false;
        }
        catch (RedisTimeoutException ex)
        {
            _logger.LogWarning(ex, "Redis timeout during revocation check for jti {Jti}. Falling back to signature+expiry validation.", jti);
            return false;
        }
    }

    private async Task RevokeToken(string jti, int ttlSeconds)
    {
        try
        {
            var db = _redis.GetDatabase();
            await db.StringSetAsync(
                $"{RevocationKeyPrefix}{jti}",
                "1",
                TimeSpan.FromSeconds(ttlSeconds));
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogWarning(ex, "Redis unavailable when revoking token {Jti}.", jti);
        }
        catch (RedisTimeoutException ex)
        {
            _logger.LogWarning(ex, "Redis timeout when revoking token {Jti}.", jti);
        }
    }

    private async Task StoreTokenFamilyAsync(string refreshJti, string familyId, int ttlSeconds)
    {
        try
        {
            var db = _redis.GetDatabase();
            var ttl = TimeSpan.FromSeconds(ttlSeconds);

            // Store jti → familyId mapping
            await db.StringSetAsync(
                $"{TokenFamilyKeyPrefix}{refreshJti}",
                familyId,
                ttl);

            // Add jti to the family members set and refresh TTL
            var familySetKey = $"{TokenFamilyMembersPrefix}{familyId}";
            await db.SetAddAsync(familySetKey, refreshJti);
            await db.KeyExpireAsync(familySetKey, ttl);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogWarning(ex, "Redis unavailable when storing token family for jti {Jti}.", refreshJti);
        }
        catch (RedisTimeoutException ex)
        {
            _logger.LogWarning(ex, "Redis timeout when storing token family for jti {Jti}.", refreshJti);
        }
    }

    private async Task UpdateTokenFamily(string refreshJti, string familyId)
    {
        try
        {
            var db = _redis.GetDatabase();
            var ttl = TimeSpan.FromSeconds(RefreshTokenExpirySeconds);

            // Store jti → familyId mapping
            await db.StringSetAsync(
                $"{TokenFamilyKeyPrefix}{refreshJti}",
                familyId,
                ttl);

            // Add jti to the family members set and refresh TTL
            var familySetKey = $"{TokenFamilyMembersPrefix}{familyId}";
            await db.SetAddAsync(familySetKey, refreshJti);
            await db.KeyExpireAsync(familySetKey, ttl);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogWarning(ex, "Redis unavailable when updating token family for jti {Jti}.", refreshJti);
        }
        catch (RedisTimeoutException ex)
        {
            _logger.LogWarning(ex, "Redis timeout when updating token family for jti {Jti}.", refreshJti);
        }
    }

    private async Task<string?> GetTokenFamily(string jti)
    {
        try
        {
            var db = _redis.GetDatabase();
            var familyId = await db.StringGetAsync($"{TokenFamilyKeyPrefix}{jti}");
            return familyId.HasValue ? familyId.ToString() : null;
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogWarning(ex, "Redis unavailable when getting token family for jti {Jti}.", jti);
            return null;
        }
        catch (RedisTimeoutException ex)
        {
            _logger.LogWarning(ex, "Redis timeout when getting token family for jti {Jti}.", jti);
            return null;
        }
    }

    private async Task RevokeTokenFamily(string jti)
    {
        try
        {
            var familyId = await GetTokenFamily(jti) ?? jti;
            var db = _redis.GetDatabase();
            var familySetKey = $"{TokenFamilyMembersPrefix}{familyId}";
            var ttl = TimeSpan.FromSeconds(RefreshTokenExpirySeconds);

            // Get all member JTIs from the family set (O(n) on family size, not keyspace)
            var members = await db.SetMembersAsync(familySetKey);
            if (members.Length > 0)
            {
                foreach (var member in members)
                {
                    var memberJti = member.ToString();
                    await db.StringSetAsync($"{RevocationKeyPrefix}{memberJti}", "1", ttl);
                }
            }

            // Also revoke the triggering jti itself in case it wasn't in the set
            await db.StringSetAsync($"{RevocationKeyPrefix}{jti}", "1", ttl);

            _logger.LogWarning("Token family {FamilyId} revoked ({MemberCount} tokens) due to refresh token reuse.", familyId, members.Length);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogWarning(ex, "Redis unavailable during family-wide revocation for jti {Jti}.", jti);
        }
        catch (RedisTimeoutException ex)
        {
            _logger.LogWarning(ex, "Redis timeout during family-wide revocation for jti {Jti}.", jti);
        }
    }

    private static JwtTokenPayload ExtractPayload(ClaimsPrincipal principal)
    {
        var sub = principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? string.Empty;

        var role = principal.FindFirstValue("role")
            ?? principal.FindFirstValue(ClaimTypes.Role)
            ?? string.Empty;

        var permissions = principal.FindAll("permissions")
            .Select(c => c.Value)
            .ToArray();

        var iatClaim = principal.FindFirstValue(JwtRegisteredClaimNames.Iat);
        var iat = iatClaim != null ? long.Parse(iatClaim) : 0;

        var expClaim = principal.FindFirstValue(JwtRegisteredClaimNames.Exp);
        var exp = expClaim != null ? long.Parse(expClaim) : 0;

        var jti = principal.FindFirstValue(JwtRegisteredClaimNames.Jti) ?? string.Empty;

        return new JwtTokenPayload
        {
            Sub = sub,
            Role = role,
            Permissions = permissions,
            Iat = iat,
            Exp = exp,
            Jti = jti
        };
    }

    private string? GetJtiFromToken(string token)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var jwtToken = handler.ReadJwtToken(token);
            return jwtToken.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Jti)?.Value;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Custom exception for token validation failures with a specific error code.
/// </summary>
public class TokenValidationException : Exception
{
    public string ErrorCode { get; }

    public TokenValidationException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }
}

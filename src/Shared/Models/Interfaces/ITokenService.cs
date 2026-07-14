using Shared.Models.DTOs;

namespace Shared.Models.Interfaces;

public record TokenPairResult(string AccessToken, string RefreshToken, int ExpiresIn);

public interface ITokenService
{
    /// <summary>
    /// Issues a new access/refresh token pair for the given user.
    /// </summary>
    TokenPairResult IssueTokenPair(string userId, string role, string[] permissions);

    /// <summary>
    /// Validates an access token and returns the decoded payload.
    /// Checks RS256 signature, expiration, and revocation status.
    /// </summary>
    Task<JwtTokenPayload> ValidateToken(string token);

    /// <summary>
    /// Refreshes a token pair using a valid refresh token.
    /// Revokes the previous refresh token (single-use rotation).
    /// </summary>
    Task<TokenPairResult> RefreshToken(string refreshToken);
}

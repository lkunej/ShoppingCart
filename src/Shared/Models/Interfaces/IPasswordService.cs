namespace Shared.Models.Interfaces;

public interface IPasswordService
{
    /// <summary>
    /// Hashes a password using Argon2id with configured parameters.
    /// </summary>
    string HashPassword(string password);

    /// <summary>
    /// Verifies a password against an Argon2id hash.
    /// </summary>
    bool VerifyPassword(string password, string hash);
}

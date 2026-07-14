using System.Security.Cryptography;
using Konscious.Security.Cryptography;
using Shared.Models.Interfaces;

namespace AuthService.Services;

public class PasswordService : IPasswordService
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int MemoryCostKB = 65536; // 64MB
    private const int Iterations = 3;
    private const int DegreeOfParallelism = 1;

    /// <inheritdoc />
    public string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = ComputeHash(password, salt);

        var result = new byte[salt.Length + hash.Length];
        Buffer.BlockCopy(salt, 0, result, 0, salt.Length);
        Buffer.BlockCopy(hash, 0, result, salt.Length, hash.Length);

        return Convert.ToBase64String(result);
    }

    /// <inheritdoc />
    public bool VerifyPassword(string password, string hash)
    {
        var decoded = Convert.FromBase64String(hash);

        if (decoded.Length != SaltSize + HashSize)
        {
            return false;
        }

        var salt = new byte[SaltSize];
        var storedHash = new byte[HashSize];

        Buffer.BlockCopy(decoded, 0, salt, 0, SaltSize);
        Buffer.BlockCopy(decoded, SaltSize, storedHash, 0, HashSize);

        var computedHash = ComputeHash(password, salt);

        return CryptographicOperations.FixedTimeEquals(computedHash, storedHash);
    }

    private static byte[] ComputeHash(string password, byte[] salt)
    {
        using var argon2 = new Argon2id(System.Text.Encoding.UTF8.GetBytes(password));
        argon2.Salt = salt;
        argon2.MemorySize = MemoryCostKB;
        argon2.Iterations = Iterations;
        argon2.DegreeOfParallelism = DegreeOfParallelism;

        return argon2.GetBytes(HashSize);
    }
}

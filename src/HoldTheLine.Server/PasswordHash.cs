using System.Security.Cryptography;

namespace HoldTheLine.Server;

/// <summary>
/// Password hashing for username accounts (docs/12 B1). PBKDF2-SHA256, 16-byte salt, 32-byte output,
/// 210,000 iterations (OWASP 2023 guidance). Self-describing string so the iteration count can be
/// raised later without breaking existing hashes: <c>pbkdf2$&lt;iters&gt;$&lt;salt b64&gt;$&lt;hash b64&gt;</c>.
/// </summary>
public static class PasswordHash
{
    private const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static string Hash(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2" || !int.TryParse(parts[1], out int iterations))
            return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

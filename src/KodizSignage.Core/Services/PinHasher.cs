using System.Security.Cryptography;
using System.Text;

namespace KodizSignage.Core.Services;

/// <summary>Salted PBKDF2 hashes for the settings PIN, stored as "salt:hash" (Base64).</summary>
public static class PinHasher
{
    private const int Iterations = 100_000;

    public static bool IsValidPin(string? pin) => pin is { Length: >= 4 and <= 12 } && pin.All(char.IsAsciiDigit);

    public static string Hash(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(Derive(pin, salt));
    }

    public static bool Verify(string? pin, string? stored)
    {
        if (pin is null || string.IsNullOrEmpty(stored))
        {
            return false;
        }

        var parts = stored.Split(':');
        if (parts.Length != 2)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[0]);
            var expected = Convert.FromBase64String(parts[1]);
            return CryptographicOperations.FixedTimeEquals(Derive(pin, salt), expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] Derive(string pin, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, Iterations, HashAlgorithmName.SHA256, 32);
}

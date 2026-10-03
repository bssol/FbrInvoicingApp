using System.Security.Cryptography;

namespace FbrInvoicingApp.Web.Services;

public sealed class PasswordService
{
    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 120_000, HashAlgorithmName.SHA256, 32);
        return $"v1.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string encoded)
    {
        var parts = encoded.Split('.');
        if (parts.Length != 3 || parts[0] != "v1") return false;
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(parts[1]), 120_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(actual, Convert.FromBase64String(parts[2]));
    }
}

using System.Security.Cryptography;
using System.Text;

namespace Musilib.Infrastructure.Auth;

/// <summary>Proof Key for Code Exchange (RFC 7636).</summary>
public static class Pkce
{
    public static string CreateVerifier()
    {
        Span<byte> bytes = stackalloc byte[48];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url(bytes);
    }

    public static string CreateChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64Url(hash);
    }

    public static string CreateState()
    {
        Span<byte> bytes = stackalloc byte[24];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url(bytes);
    }

    public static string Base64Url(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

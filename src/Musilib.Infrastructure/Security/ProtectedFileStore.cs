using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Musilib.Application.Abstractions;

namespace Musilib.Infrastructure.Security;

/// <summary>
/// Stockage chiffré des secrets et jetons. Windows : DPAPI (portée utilisateur courant).
/// Autres systèmes : AES-GCM avec une clé locale protégée par les permissions du fichier (0600).
/// </summary>
public sealed class ProtectedFileStore : ISecureStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Musilib.Manager.SecureStore.v1");
    private readonly string _directory;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ProtectedFileStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(_directory);
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        var path = PathFor(key);
        if (!File.Exists(path)) return null;
        var data = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        try
        {
            return Encoding.UTF8.GetString(Unprotect(data));
        }
        catch (CryptographicException)
        {
            // Fichier corrompu ou chiffré pour un autre utilisateur : ignoré.
            return null;
        }
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        var bytes = Protect(Encoding.UTF8.GetBytes(value));
        var path = PathFor(key);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
            RestrictPermissions(temp);
            File.Move(temp, path, overwrite: true);
        }
        finally { _lock.Release(); }
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var path = PathFor(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private string PathFor(string key)
    {
        var safe = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32].ToLowerInvariant();
        return Path.Combine(_directory, safe + ".bin");
    }

    private byte[] Protect(byte[] plain)
    {
        if (OperatingSystem.IsWindows()) return ProtectWindows(plain);
        var key = GetOrCreateLocalKey();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, cipher, tag, Entropy);
        return [.. nonce, .. tag, .. cipher];
    }

    private byte[] Unprotect(byte[] data)
    {
        if (OperatingSystem.IsWindows()) return UnprotectWindows(data);
        var key = GetOrCreateLocalKey();
        var nonce = data.AsSpan(0, 12);
        var tag = data.AsSpan(12, 16);
        var cipher = data.AsSpan(28);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, cipher, tag, plain, Entropy);
        return plain;
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ProtectWindows(byte[] plain) => ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectWindows(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);

    private byte[] GetOrCreateLocalKey()
    {
        var path = Path.Combine(_directory, "local.key");
        if (File.Exists(path)) return File.ReadAllBytes(path);
        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(path, key);
        RestrictPermissions(path);
        return key;
    }

    private static void RestrictPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}

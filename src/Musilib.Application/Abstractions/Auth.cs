namespace Musilib.Application.Abstractions;

public enum ExternalService
{
    YouTube,
    Spotify
}

public sealed record ConnectionInfo(
    ExternalService Provider,
    bool IsConfigured,
    bool IsConnected,
    string? AccountName,
    DateTimeOffset? ExpiresAt,
    string? Error = null,
    bool IsDemo = false);

/// <summary>Jeton OAuth persisté de façon chiffrée.</summary>
public sealed class TokenSet
{
    public string AccessToken { get; set; } = string.Empty;
    public string? RefreshToken { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string? Scope { get; set; }
    public string? AccountName { get; set; }

    public bool IsExpired(DateTimeOffset now, TimeSpan margin) => now + margin >= ExpiresAt;
}

/// <summary>Service d'authentification OAuth d'un fournisseur.</summary>
public interface IAuthService
{
    ExternalService Provider { get; }
    Task<ConnectionInfo> GetStatusAsync(CancellationToken ct);
    /// <summary>Lance le flux OAuth interactif (navigateur système + redirection loopback).</summary>
    Task<ConnectionInfo> SignInAsync(CancellationToken ct);
    Task SignOutAsync(CancellationToken ct);
    /// <summary>Jeton d'accès valide, renouvelé automatiquement si nécessaire.</summary>
    Task<string> GetAccessTokenAsync(CancellationToken ct);
    /// <summary>Force le renouvellement (après un 401).</summary>
    Task<string> RefreshAccessTokenAsync(CancellationToken ct);
    Task SetAccountNameAsync(string? accountName, CancellationToken ct);
}

/// <summary>Stockage chiffré de secrets (DPAPI sous Windows).</summary>
public interface ISecureStore
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string value, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
}

public static class SecretKeys
{
    public const string GoogleClientSecret = "google.client_secret";
    public const string YouTubeApiKey = "youtube.api_key";
    public const string YouTubeTokens = "youtube.tokens";
    public const string SpotifyTokens = "spotify.tokens";
}

/// <summary>Ouvre une URL dans le navigateur système.</summary>
public interface IBrowserLauncher
{
    void Open(string url);
}

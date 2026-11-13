using Microsoft.Extensions.Logging;
using Musilib.Application.Abstractions;
using Musilib.Application.Errors;

namespace Musilib.Infrastructure.Auth;

/// <summary>
/// OAuth Google (client "Application de bureau") : PKCE + redirection loopback sur un port libre,
/// portée youtube.readonly. Google exige le client secret des clients de bureau ; il est stocké chiffré.
/// </summary>
public sealed class GoogleAuthService : OAuthServiceBase
{
    private readonly ISettingsService _settings;
    private readonly ISecureStore _store;

    public GoogleAuthService(ISettingsService settings, ISecureStore store, IBrowserLauncher browser, IHttpClientFactory http,
        ILogger<GoogleAuthService> logger, TimeProvider time)
        : base(store, browser, http, logger, time)
    {
        _settings = settings;
        _store = store;
    }

    public override ExternalService Provider => ExternalService.YouTube;
    protected override string TokenKey => SecretKeys.YouTubeTokens;
    protected override string TokenEndpoint => _settings.Current.YouTube.TokenEndpoint;
    protected override string CallbackPath => "/callback";
    protected override int RedirectPort => 0;

    protected override async Task<bool> IsConfiguredAsync(CancellationToken ct) =>
        !string.IsNullOrWhiteSpace(_settings.Current.YouTube.ClientId)
        && !string.IsNullOrWhiteSpace(await _store.GetAsync(SecretKeys.GoogleClientSecret, ct).ConfigureAwait(false));

    protected override string BuildRedirectUri(int port) => $"http://127.0.0.1:{port}/callback";

    protected override string BuildAuthorizeUrl(string redirectUri, string state, string codeChallenge)
    {
        var s = _settings.Current.YouTube;
        return s.AuthorizationEndpoint + "?" + Query(new()
        {
            ["client_id"] = s.ClientId!,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = s.Scope,
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["access_type"] = "offline",
            ["prompt"] = "consent"
        });
    }

    protected override async Task<Dictionary<string, string>> BuildCodeExchangeAsync(string code, string redirectUri, string verifier, CancellationToken ct) => new()
    {
        ["client_id"] = _settings.Current.YouTube.ClientId!,
        ["client_secret"] = await GetSecretAsync(ct).ConfigureAwait(false),
        ["code"] = code,
        ["code_verifier"] = verifier,
        ["grant_type"] = "authorization_code",
        ["redirect_uri"] = redirectUri
    };

    protected override async Task<Dictionary<string, string>> BuildRefreshAsync(string refreshToken, CancellationToken ct) => new()
    {
        ["client_id"] = _settings.Current.YouTube.ClientId!,
        ["client_secret"] = await GetSecretAsync(ct).ConfigureAwait(false),
        ["refresh_token"] = refreshToken,
        ["grant_type"] = "refresh_token"
    };

    protected override async Task RevokeAsync(Application.Abstractions.TokenSet tokens, CancellationToken ct)
    {
        using var client = CreateHttpClient();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = tokens.RefreshToken ?? tokens.AccessToken });
        using var _ = await client.PostAsync(_settings.Current.YouTube.RevocationEndpoint, content, ct).ConfigureAwait(false);
    }

    private async Task<string> GetSecretAsync(CancellationToken ct) =>
        await _store.GetAsync(SecretKeys.GoogleClientSecret, ct).ConfigureAwait(false)
        ?? throw new ConfigurationMissingException("Client secret Google manquant.");

    internal static string Query(Dictionary<string, string> values) =>
        string.Join("&", values.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
}

/// <summary>
/// OAuth Spotify : Authorization Code + PKCE (aucun secret client nécessaire).
/// L'URI de redirection http://127.0.0.1:{port}/callback doit être déclarée dans le Dashboard Spotify.
/// </summary>
public sealed class SpotifyAuthService : OAuthServiceBase
{
    private readonly ISettingsService _settings;

    public SpotifyAuthService(ISettingsService settings, ISecureStore store, IBrowserLauncher browser, IHttpClientFactory http,
        ILogger<SpotifyAuthService> logger, TimeProvider time)
        : base(store, browser, http, logger, time)
    {
        _settings = settings;
    }

    public override ExternalService Provider => ExternalService.Spotify;
    protected override string TokenKey => SecretKeys.SpotifyTokens;
    protected override string TokenEndpoint => _settings.Current.Spotify.TokenEndpoint;
    protected override string CallbackPath => "/callback";
    protected override int RedirectPort => _settings.Current.Spotify.RedirectPort;

    protected override Task<bool> IsConfiguredAsync(CancellationToken ct) =>
        Task.FromResult(!string.IsNullOrWhiteSpace(_settings.Current.Spotify.ClientId));

    protected override string BuildRedirectUri(int port) => $"http://127.0.0.1:{port}/callback";

    protected override string BuildAuthorizeUrl(string redirectUri, string state, string codeChallenge)
    {
        var s = _settings.Current.Spotify;
        return s.AuthorizationEndpoint + "?" + GoogleAuthService.Query(new()
        {
            ["client_id"] = s.ClientId!,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["scope"] = s.Scope,
            ["state"] = state,
            ["code_challenge_method"] = "S256",
            ["code_challenge"] = codeChallenge
        });
    }

    protected override Task<Dictionary<string, string>> BuildCodeExchangeAsync(string code, string redirectUri, string verifier, CancellationToken ct) =>
        Task.FromResult(new Dictionary<string, string>
        {
            ["client_id"] = _settings.Current.Spotify.ClientId!,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier
        });

    protected override Task<Dictionary<string, string>> BuildRefreshAsync(string refreshToken, CancellationToken ct) =>
        Task.FromResult(new Dictionary<string, string>
        {
            ["client_id"] = _settings.Current.Spotify.ClientId!,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken
        });
}

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Musilib.Application.Abstractions;
using Musilib.Application.Common;
using Musilib.Application.Errors;

namespace Musilib.Infrastructure.Auth;

/// <summary>
/// Flux OAuth 2.0 "Authorization Code + PKCE" avec redirection loopback, stockage chiffré des jetons
/// et renouvellement automatique (thread-safe). Les jetons ne sont jamais journalisés.
/// </summary>
public abstract class OAuthServiceBase : IAuthService
{
    public static readonly TimeSpan ExpiryMargin = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    private readonly ISecureStore _store;
    private readonly IBrowserLauncher _browser;
    private readonly IHttpClientFactory _httpFactory;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private TokenSet? _cached;
    private bool _loaded;

    protected OAuthServiceBase(ISecureStore store, IBrowserLauncher browser, IHttpClientFactory httpFactory, ILogger logger, TimeProvider time)
    {
        _store = store;
        _browser = browser;
        _httpFactory = httpFactory;
        Logger = logger;
        _time = time;
    }

    protected ILogger Logger { get; }
    public abstract ExternalService Provider { get; }
    protected abstract string TokenKey { get; }
    protected abstract string TokenEndpoint { get; }
    protected abstract string CallbackPath { get; }
    /// <summary>Port de redirection (0 = port libre aléatoire).</summary>
    protected abstract int RedirectPort { get; }
    protected abstract Task<bool> IsConfiguredAsync(CancellationToken ct);
    protected abstract string BuildRedirectUri(int port);
    protected abstract string BuildAuthorizeUrl(string redirectUri, string state, string codeChallenge);
    protected abstract Task<Dictionary<string, string>> BuildCodeExchangeAsync(string code, string redirectUri, string verifier, CancellationToken ct);
    protected abstract Task<Dictionary<string, string>> BuildRefreshAsync(string refreshToken, CancellationToken ct);
    protected virtual Task RevokeAsync(TokenSet tokens, CancellationToken ct) => Task.CompletedTask;

    protected HttpClient CreateHttpClient() => _httpFactory.CreateClient("oauth");

    public async Task<ConnectionInfo> GetStatusAsync(CancellationToken ct)
    {
        var configured = await IsConfiguredAsync(ct).ConfigureAwait(false);
        var tokens = await LoadAsync(ct).ConfigureAwait(false);
        var connected = tokens is not null && (!string.IsNullOrEmpty(tokens.RefreshToken) || !tokens.IsExpired(_time.GetUtcNow(), TimeSpan.Zero));
        return new ConnectionInfo(Provider, configured, connected, tokens?.AccountName, tokens?.ExpiresAt);
    }

    public async Task<ConnectionInfo> SignInAsync(CancellationToken ct)
    {
        if (!await IsConfiguredAsync(ct).ConfigureAwait(false))
            throw new ConfigurationMissingException($"Renseignez l'identifiant client {Provider} dans l'écran Connexions.");

        var verifier = Pkce.CreateVerifier();
        var state = Pkce.CreateState();
        using var listener = new LoopbackCallbackListener(RedirectPort);
        var redirectUri = BuildRedirectUri(listener.Port);
        var url = BuildAuthorizeUrl(redirectUri, state, Pkce.CreateChallenge(verifier));

        Logger.LogInformation("Ouverture du navigateur pour l'authentification {Provider}", Provider);
        _browser.Open(url);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SignInTimeout);
        OAuthCallback callback;
        try
        {
            callback = await listener.WaitForCallbackAsync(CallbackPath, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AuthenticationRequiredException(Provider.ToString(), "Délai d'authentification dépassé.");
        }

        if (callback.Error is not null)
            throw new AuthenticationRequiredException(Provider.ToString(), $"Autorisation refusée : {callback.ErrorDescription ?? callback.Error}");
        if (!string.Equals(callback.State, state, StringComparison.Ordinal))
            throw new AuthenticationRequiredException(Provider.ToString(), "Réponse d'autorisation invalide (paramètre state).");
        if (string.IsNullOrEmpty(callback.Code))
            throw new AuthenticationRequiredException(Provider.ToString(), "Aucun code d'autorisation reçu.");

        var form = await BuildCodeExchangeAsync(callback.Code, redirectUri, verifier, ct).ConfigureAwait(false);
        var tokens = await RequestTokenAsync(form, null, ct).ConfigureAwait(false);
        await SaveAsync(tokens, ct).ConfigureAwait(false);
        Logger.LogInformation("Connexion {Provider} établie", Provider);
        return await GetStatusAsync(ct).ConfigureAwait(false);
    }

    public async Task SignOutAsync(CancellationToken ct)
    {
        var tokens = await LoadAsync(ct).ConfigureAwait(false);
        if (tokens is not null)
        {
            try { await RevokeAsync(tokens, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                Logger.LogWarning("Révocation du jeton {Provider} impossible : {Message}", Provider, ex.Message);
            }
        }
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _cached = null;
            _loaded = true;
            await _store.DeleteAsync(TokenKey, ct).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
        Logger.LogInformation("Déconnexion {Provider}", Provider);
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        var tokens = await LoadAsync(ct).ConfigureAwait(false)
                     ?? throw new AuthenticationRequiredException(Provider.ToString(), $"Compte {Provider} non connecté.");
        if (!tokens.IsExpired(_time.GetUtcNow(), ExpiryMargin)) return tokens.AccessToken;
        return await RefreshAccessTokenAsync(ct).ConfigureAwait(false);
    }

    public async Task<string> RefreshAccessTokenAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = _cached ?? await LoadUnlockedAsync(ct).ConfigureAwait(false);
            if (current is null)
                throw new AuthenticationRequiredException(Provider.ToString(), $"Compte {Provider} non connecté.");
            // Un autre appel concurrent vient peut-être de renouveler le jeton.
            if (!current.IsExpired(_time.GetUtcNow(), ExpiryMargin) && current.ExpiresAt - _time.GetUtcNow() > TimeSpan.FromMinutes(50))
                return current.AccessToken;
            if (string.IsNullOrEmpty(current.RefreshToken))
                throw new AuthenticationRequiredException(Provider.ToString(), "Session expirée : reconnectez-vous.");

            var form = await BuildRefreshAsync(current.RefreshToken, ct).ConfigureAwait(false);
            TokenSet refreshed;
            try
            {
                refreshed = await RequestTokenAsync(form, current, ct).ConfigureAwait(false);
            }
            catch (AuthenticationRequiredException)
            {
                _cached = null;
                await _store.DeleteAsync(TokenKey, ct).ConfigureAwait(false);
                throw;
            }
            _cached = refreshed;
            await _store.SetAsync(TokenKey, JsonDefaults.Serialize(refreshed), ct).ConfigureAwait(false);
            Logger.LogDebug("Jeton {Provider} renouvelé", Provider);
            return refreshed.AccessToken;
        }
        finally { _lock.Release(); }
    }

    public async Task SetAccountNameAsync(string? accountName, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tokens = _cached ?? await LoadUnlockedAsync(ct).ConfigureAwait(false);
            if (tokens is null) return;
            tokens.AccountName = accountName;
            _cached = tokens;
            await _store.SetAsync(TokenKey, JsonDefaults.Serialize(tokens), ct).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    private async Task<TokenSet> RequestTokenAsync(Dictionary<string, string> form, TokenSet? previous, CancellationToken ct)
    {
        using var client = CreateHttpClient();
        using var content = new FormUrlEncodedContent(form);
        using var response = await client.PostAsync(TokenEndpoint, content, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string? error = null, description = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                error = doc.RootElement.TryGetProperty("error", out var e) ? e.ToString() : null;
                description = doc.RootElement.TryGetProperty("error_description", out var d) ? d.GetString() : null;
            }
            catch (JsonException) { }

            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized && error is "invalid_grant" or "invalid_client" or "unauthorized_client")
                throw new AuthenticationRequiredException(Provider.ToString(), $"Autorisation refusée ({error}) : reconnectez-vous. {description}");
            throw new ApiException(Provider.ToString(), $"Échec de l'obtention du jeton ({(int)response.StatusCode}) : {description ?? error}", response.StatusCode, error);
        }

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var expiresIn = root.TryGetProperty("expires_in", out var exp) && exp.TryGetInt32(out var s) ? s : 3600;
        return new TokenSet
        {
            AccessToken = root.GetProperty("access_token").GetString()!,
            RefreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : previous?.RefreshToken,
            Scope = root.TryGetProperty("scope", out var sc) ? sc.GetString() : previous?.Scope,
            ExpiresAt = _time.GetUtcNow().AddSeconds(expiresIn),
            AccountName = previous?.AccountName
        };
    }

    private async Task<TokenSet?> LoadAsync(CancellationToken ct)
    {
        if (_loaded) return _cached;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try { return await LoadUnlockedAsync(ct).ConfigureAwait(false); }
        finally { _lock.Release(); }
    }

    private async Task<TokenSet?> LoadUnlockedAsync(CancellationToken ct)
    {
        if (_loaded) return _cached;
        var json = await _store.GetAsync(TokenKey, ct).ConfigureAwait(false);
        try { _cached = JsonDefaults.Deserialize<TokenSet>(json); }
        catch (JsonException) { _cached = null; }
        _loaded = true;
        return _cached;
    }

    private async Task SaveAsync(TokenSet tokens, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            tokens.AccountName ??= _cached?.AccountName;
            _cached = tokens;
            _loaded = true;
            await _store.SetAsync(TokenKey, JsonDefaults.Serialize(tokens), ct).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }
}

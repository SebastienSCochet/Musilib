using System.Net;
using System.Net.Http.Headers;
using Musilib.Application.Abstractions;
using Musilib.Application.Errors;

namespace Musilib.Infrastructure.Http;

/// <summary>Ajoute le jeton Bearer et tente un renouvellement unique en cas de 401.</summary>
public sealed class BearerTokenHandler : DelegatingHandler
{
    private readonly IAuthService _auth;

    public BearerTokenHandler(IAuthService auth) => _auth = auth;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await _auth.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;

        response.Dispose();
        token = await _auth.RefreshAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// YouTube : utilise le jeton OAuth si l'utilisateur est connecté, sinon la clé API (playlists publiques uniquement).
/// La clé est transmise par en-tête (X-goog-api-key) pour ne jamais apparaître dans les URL journalisées.
/// </summary>
public sealed class YouTubeAuthHandler : DelegatingHandler
{
    public const string RequiresUserOption = "musilib.requires-user";

    private readonly IAuthService _auth;
    private readonly ISecureStore _secrets;

    public YouTubeAuthHandler(IAuthService auth, ISecureStore secrets)
    {
        _auth = auth;
        _secrets = secrets;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var status = await _auth.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.IsConnected)
        {
            var inner = new BearerTokenHandlerAdapter(_auth);
            return await inner.SendWithTokenAsync(request, r => base.SendAsync(r, cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        request.Options.TryGetValue(new HttpRequestOptionsKey<bool>(RequiresUserOption), out var requiresUser);
        var apiKey = await _secrets.GetAsync(SecretKeys.YouTubeApiKey, cancellationToken).ConfigureAwait(false);
        if (requiresUser || string.IsNullOrWhiteSpace(apiKey))
            throw new AuthenticationRequiredException("YouTube", "Connectez votre compte YouTube (ou configurez une clé API pour les playlists publiques).");

        request.Headers.Remove("X-goog-api-key");
        request.Headers.Add("X-goog-api-key", apiKey);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private sealed class BearerTokenHandlerAdapter
    {
        private readonly IAuthService _auth;
        public BearerTokenHandlerAdapter(IAuthService auth) => _auth = auth;

        public async Task<HttpResponseMessage> SendWithTokenAsync(HttpRequestMessage request, Func<HttpRequestMessage, Task<HttpResponseMessage>> send, CancellationToken ct)
        {
            var token = await _auth.GetAccessTokenAsync(ct).ConfigureAwait(false);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await send(request).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Unauthorized) return response;
            response.Dispose();
            token = await _auth.RefreshAccessTokenAsync(ct).ConfigureAwait(false);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await send(request).ConfigureAwait(false);
        }
    }
}

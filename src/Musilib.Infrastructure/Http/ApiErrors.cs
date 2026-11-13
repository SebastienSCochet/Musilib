using System.Net;
using System.Text.Json;
using Musilib.Application.Errors;

namespace Musilib.Infrastructure.Http;

internal static class ApiErrors
{
    private static readonly HashSet<string> GoogleQuotaReasons = new(StringComparer.OrdinalIgnoreCase)
    {
        "quotaExceeded", "dailyLimitExceeded", "rateLimitExceeded", "userRateLimitExceeded"
    };

    /// <summary>Convertit une réponse d'erreur YouTube Data API en exception typée.</summary>
    public static async Task<Exception> FromGoogleAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await SafeReadAsync(response, ct).ConfigureAwait(false);
        string? reason = null;
        var message = response.ReasonPhrase ?? "Erreur";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.TryGetProperty("message", out var m)) message = m.GetString() ?? message;
                if (error.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0
                    && errors[0].TryGetProperty("reason", out var r))
                    reason = r.GetString();
            }
        }
        catch (JsonException) { }

        message = StripHtml(message);
        if (response.StatusCode == HttpStatusCode.Forbidden && reason is not null && GoogleQuotaReasons.Contains(reason))
            return new QuotaExceededException("YouTube", reason is "quotaExceeded" or "dailyLimitExceeded"
                ? "Quota journalier de l'API YouTube épuisé (réinitialisé à minuit, heure du Pacifique)."
                : "Limite de débit YouTube atteinte.", RetryHandler.GetRetryAfter(response));

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return new AuthenticationRequiredException("YouTube", "Session YouTube expirée : reconnectez-vous.");

        var friendly = reason switch
        {
            "playlistNotFound" => "Playlist introuvable ou privée.",
            "playlistItemsNotAccessible" => "Vous n'avez pas accès aux éléments de cette playlist.",
            "channelNotFound" => "Chaîne YouTube introuvable pour ce compte.",
            "keyInvalid" or "badRequest" when message.Contains("API key", StringComparison.OrdinalIgnoreCase) => "Clé API YouTube invalide.",
            _ => message
        };
        return new ApiException("YouTube", friendly, response.StatusCode, reason, RetryHandler.GetRetryAfter(response));
    }

    /// <summary>Convertit une réponse d'erreur Spotify Web API en exception typée.</summary>
    public static async Task<Exception> FromSpotifyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await SafeReadAsync(response, ct).ConfigureAwait(false);
        var message = response.ReasonPhrase ?? "Erreur";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m)) message = m.GetString() ?? message;
                else if (error.ValueKind == JsonValueKind.String) message = error.GetString() ?? message;
            }
        }
        catch (JsonException) { }

        var retryAfter = RetryHandler.GetRetryAfter(response);
        return response.StatusCode switch
        {
            HttpStatusCode.TooManyRequests => new QuotaExceededException("Spotify",
                $"Limite de débit Spotify atteinte{(retryAfter is { } ra ? $" (réessayer dans {Math.Ceiling(ra.TotalMinutes)} min)" : string.Empty)}.", retryAfter),
            HttpStatusCode.Unauthorized => new AuthenticationRequiredException("Spotify", "Session Spotify expirée : reconnectez-vous."),
            HttpStatusCode.Forbidden => new ApiException("Spotify",
                $"Accès refusé par Spotify : {message}. En mode développement, votre compte doit être ajouté aux utilisateurs de l'application (Dashboard Spotify).",
                response.StatusCode),
            _ => new ApiException("Spotify", message, response.StatusCode, null, retryAfter)
        };
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or IOException) { return string.Empty; }
    }

    private static string StripHtml(string text) => System.Text.RegularExpressions.Regex.Replace(text, "<.*?>", string.Empty);
}

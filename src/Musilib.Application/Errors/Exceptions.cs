using System.Net;

namespace Musilib.Application.Errors;

/// <summary>Erreur renvoyée par une API externe.</summary>
public class ApiException : Exception
{
    public ApiException(string provider, string message, HttpStatusCode? statusCode = null, string? reason = null, TimeSpan? retryAfter = null, Exception? inner = null)
        : base(message, inner)
    {
        Provider = provider;
        StatusCode = statusCode;
        Reason = reason;
        RetryAfter = retryAfter;
    }

    public string Provider { get; }
    public HttpStatusCode? StatusCode { get; }
    public string? Reason { get; }
    public TimeSpan? RetryAfter { get; }

    public bool IsTransient => StatusCode is null
        or HttpStatusCode.TooManyRequests
        or HttpStatusCode.RequestTimeout
        or >= HttpStatusCode.InternalServerError;
}

/// <summary>Quota journalier épuisé (YouTube) ou limite de débit persistante : le traitement est mis en pause.</summary>
public sealed class QuotaExceededException : ApiException
{
    public QuotaExceededException(string provider, string message, TimeSpan? retryAfter = null)
        : base(provider, message, HttpStatusCode.Forbidden, "quotaExceeded", retryAfter) { }
}

/// <summary>Une connexion (ou reconnexion) au compte est nécessaire.</summary>
public sealed class AuthenticationRequiredException : Exception
{
    public AuthenticationRequiredException(string provider, string message, Exception? inner = null) : base(message, inner)
    {
        Provider = provider;
    }

    public string Provider { get; }
}

/// <summary>Configuration manquante (identifiant client, clé API...).</summary>
public sealed class ConfigurationMissingException : Exception
{
    public ConfigurationMissingException(string message) : base(message) { }
}

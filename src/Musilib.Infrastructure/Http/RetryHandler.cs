using System.Net;
using Microsoft.Extensions.Logging;
using Musilib.Application.Settings;

namespace Musilib.Infrastructure.Http;

/// <summary>
/// Réessaie les requêtes en échec transitoire (429, 408, 5xx, erreurs réseau) avec backoff exponentiel
/// et jitter, en respectant l'en-tête Retry-After. Les autres erreurs sont renvoyées telles quelles.
/// </summary>
public sealed class RetryHandler : DelegatingHandler
{
    public static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(800);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly Func<ProcessingSettings> _settings;
    private readonly ILogger _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public RetryHandler(Func<ProcessingSettings> settings, ILogger logger, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _settings = settings;
        _logger = logger;
        _delay = delay ?? Task.Delay;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var settings = _settings();
        var maxRetries = Math.Max(0, settings.MaxRetries);
        var maxRetryAfter = TimeSpan.FromSeconds(Math.Max(1, settings.MaxRetryAfterSeconds));

        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < maxRetries && !cancellationToken.IsCancellationRequested
                                       && ex is HttpRequestException or TaskCanceledException or IOException)
            {
                var wait = Backoff(attempt);
                _logger.LogWarning("Erreur réseau ({Error}), nouvelle tentative {Attempt}/{Max} dans {Delay} ms",
                    ex.GetType().Name, attempt + 1, maxRetries, (int)wait.TotalMilliseconds);
                await _delay(wait, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!IsTransient(response.StatusCode) || attempt >= maxRetries) return response;

            var retryAfter = GetRetryAfter(response);
            if (retryAfter is { } ra && ra > maxRetryAfter)
            {
                // Attente trop longue : on remonte la réponse, l'appelant mettra le traitement en pause.
                _logger.LogWarning("Retry-After de {Seconds} s supérieur au maximum autorisé", (int)ra.TotalSeconds);
                return response;
            }

            var delay = retryAfter ?? Backoff(attempt);
            _logger.LogInformation("Réponse {Status} de {Host}, nouvelle tentative {Attempt}/{Max} dans {Delay} ms",
                (int)response.StatusCode, request.RequestUri?.Host, attempt + 1, maxRetries, (int)delay.TotalMilliseconds);
            response.Dispose();
            await _delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    public static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout
            or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    public static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null) return null;
        if (header.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (header.Date is { } date)
        {
            var d = date - DateTimeOffset.UtcNow;
            return d < TimeSpan.Zero ? TimeSpan.Zero : d;
        }
        return null;
    }

    public static TimeSpan Backoff(int attempt)
    {
        var exp = BaseDelay.TotalMilliseconds * Math.Pow(2, Math.Min(attempt, 10));
        var jitter = Random.Shared.NextDouble() * BaseDelay.TotalMilliseconds;
        return TimeSpan.FromMilliseconds(Math.Min(exp + jitter, MaxBackoff.TotalMilliseconds));
    }
}

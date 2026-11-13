using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Web;

namespace Musilib.Infrastructure.Auth;

public sealed record OAuthCallback(string? Code, string? State, string? Error, string? ErrorDescription);

/// <summary>
/// Serveur HTTP minimal sur 127.0.0.1 recevant la redirection OAuth (flux "loopback" recommandé pour les
/// applications de bureau par Google et Spotify). N'utilise pas HttpListener : aucun droit administrateur requis.
/// </summary>
public sealed class LoopbackCallbackListener : IDisposable
{
    private readonly TcpListener _listener;

    public LoopbackCallbackListener(int port)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    public async Task<OAuthCallback> WaitForCallbackAsync(string expectedPath, CancellationToken ct)
    {
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            try
            {
                var callback = await HandleConnectionAsync(client, expectedPath, ct).ConfigureAwait(false);
                if (callback is not null) return callback;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // Connexion fermée prématurément ou spéculative : on attend la suivante.
            }
        }
    }

    private static async Task<OAuthCallback?> HandleConnectionAsync(TcpClient client, string expectedPath, CancellationToken ct)
    {
        await using var stream = client.GetStream();
        // Les navigateurs ouvrent parfois des connexions spéculatives sans requête : délai court par connexion.
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectionCts.CancelAfter(TimeSpan.FromSeconds(5));
        var requestLine = await ReadRequestLineAsync(stream, connectionCts.Token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(requestLine)) return null;

        // "GET /callback?code=...&state=... HTTP/1.1"
        var parts = requestLine.Split(' ');
        if (parts.Length < 2 || parts[0] != "GET")
        {
            await WriteResponseAsync(stream, 400, "Requête invalide", connectionCts.Token).ConfigureAwait(false);
            return null;
        }

        var target = parts[1];
        var path = target.Split('?')[0];
        if (!string.Equals(path.TrimEnd('/'), expectedPath.TrimEnd('/'), StringComparison.Ordinal))
        {
            await WriteResponseAsync(stream, 404, "Introuvable", connectionCts.Token).ConfigureAwait(false);
            return null;
        }

        var query = HttpUtility.ParseQueryString(target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : string.Empty);
        var callback = new OAuthCallback(query["code"], query["state"], query["error"], query["error_description"]);
        var ok = callback.Error is null && callback.Code is not null;
        try
        {
            await WriteResponseAsync(stream, 200, ok
                ? "Connexion réussie. Vous pouvez fermer cet onglet et revenir à Musilib Manager."
                : "La connexion a échoué ou a été refusée. Vous pouvez fermer cet onglet.", connectionCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
            // Le code est reçu : l'échec de la page de confirmation n'empêche pas la connexion.
        }
        return callback;
    }

    private static async Task<string> ReadRequestLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var sb = new StringBuilder();
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0) break;
            sb.Append(Encoding.ASCII.GetString(buffer, 0, read));
            var text = sb.ToString();
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal) || sb.Length > 64 * 1024) break;
        }
        var all = sb.ToString();
        var end = all.IndexOf("\r\n", StringComparison.Ordinal);
        return end >= 0 ? all[..end] : all;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int status, string message, CancellationToken ct)
    {
        var html = $"""
            <!doctype html><html lang="fr"><head><meta charset="utf-8"><title>Musilib Manager</title>
            <style>body{"{"}font-family:Segoe UI,sans-serif;background:#0f1115;color:#e8eaf0;display:flex;align-items:center;justify-content:center;height:100vh;margin:0{"}"}
            .card{"{"}background:#1a1d24;padding:40px 48px;border-radius:16px;max-width:520px;text-align:center{"}"}h1{"{"}color:#1ed760;font-size:22px{"}"}</style></head>
            <body><div class="card"><h1>Musilib Manager</h1><p>{WebUtility.HtmlEncode(message)}</p></div></body></html>
            """;
        var body = Encoding.UTF8.GetBytes(html);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public void Dispose() => _listener.Stop();
}

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Musilib.Application.Abstractions;

namespace Musilib.Infrastructure.Media;

/// <summary>Téléchargement des pochettes/miniatures avec cache disque (une seule requête par URL).</summary>
public sealed class CachedImageDownloader : IImageDownloader
{
    public const string HttpClientName = "images";
    private const long MaxImageBytes = 10 * 1024 * 1024;

    private readonly IHttpClientFactory _factory;
    private readonly ILogger<CachedImageDownloader> _logger;
    private readonly string _directory;
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _inflight = new();

    public CachedImageDownloader(IHttpClientFactory factory, IAppPaths paths, ILogger<CachedImageDownloader> logger)
    {
        _factory = factory;
        _logger = logger;
        _directory = Path.Combine(paths.CacheDirectory, "images");
        Directory.CreateDirectory(_directory);
    }

    public async Task<(byte[] Data, string MimeType)?> GetAsync(string url, CancellationToken ct = default)
    {
        var path = await GetCachedPathAsync(url, ct).ConfigureAwait(false);
        if (path is null) return null;
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        return (bytes, DetectMime(bytes));
    }

    public Task<string?> GetCachedPathAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return Task.FromResult<string?>(null);
        if (File.Exists(url)) return Task.FromResult<string?>(url);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return Task.FromResult<string?>(null);

        var file = Path.Combine(_directory, Hash(url) + ".img");
        if (File.Exists(file)) return Task.FromResult<string?>(file);

        var lazy = _inflight.GetOrAdd(url, _ => new Lazy<Task<string?>>(() => DownloadAsync(uri, file)));
        return lazy.Value.WaitAsync(ct);
    }

    private async Task<string?> DownloadAsync(Uri uri, string file)
    {
        try
        {
            var client = _factory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            if (response.Content.Headers.ContentLength is > MaxImageBytes) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (bytes.Length == 0 || bytes.Length > MaxImageBytes) return null;
            var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(false);
            File.Move(temp, file, overwrite: true);
            return file;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            _logger.LogDebug("Image indisponible ({Host}) : {Message}", uri.Host, ex.Message);
            return null;
        }
        finally
        {
            _inflight.TryRemove(uri.OriginalString, out _);
        }
    }

    public static string DetectMime(byte[] data)
    {
        if (data.Length > 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return "image/png";
        if (data.Length > 12 && data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P') return "image/webp";
        return "image/jpeg";
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..40].ToLowerInvariant();
}

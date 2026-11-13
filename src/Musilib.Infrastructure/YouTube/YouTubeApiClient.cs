using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml;
using Microsoft.Extensions.Logging;
using Musilib.Application.Abstractions;
using Musilib.Domain.Enums;
using Musilib.Infrastructure.Http;

namespace Musilib.Infrastructure.YouTube;

/// <summary>
/// Client YouTube Data API v3 (lecture seule). Pagination complète (maxResults=50), récupération des durées
/// par lots de 50 vidéos et détection des vidéos supprimées, privées ou indisponibles.
/// Coût en quota : 1 unité par page de playlistItems + 1 unité par lot de videos.
/// </summary>
public sealed class YouTubeApiClient : IYouTubeClient
{
    public const string HttpClientName = "youtube";
    private const int PageSize = 50;

    private readonly IHttpClientFactory _factory;
    private readonly ILogger<YouTubeApiClient> _logger;

    public YouTubeApiClient(IHttpClientFactory factory, ILogger<YouTubeApiClient> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<string?> GetChannelTitleAsync(CancellationToken ct)
    {
        using var doc = await GetAsync("channels?part=snippet&mine=true", requiresUser: true, ct).ConfigureAwait(false);
        var items = doc.RootElement.GetProperty("items");
        return items.GetArrayLength() > 0 ? items[0].GetProperty("snippet").GetProperty("title").GetString() : null;
    }

    public async Task<IReadOnlyList<RemotePlaylist>> GetMyPlaylistsAsync(CancellationToken ct)
    {
        var list = new List<RemotePlaylist>();
        string? pageToken = null;
        do
        {
            var url = $"playlists?part=snippet,contentDetails&mine=true&maxResults={PageSize}" + PageParam(pageToken);
            using var doc = await GetAsync(url, requiresUser: true, ct).ConfigureAwait(false);
            foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
                list.Add(MapPlaylist(item));
            pageToken = NextPage(doc);
        } while (pageToken is not null);

        // Playlist "J'aime" (accessible uniquement à son propriétaire)
        try
        {
            using var channel = await GetAsync("channels?part=contentDetails&mine=true", requiresUser: true, ct).ConfigureAwait(false);
            var items = channel.RootElement.GetProperty("items");
            if (items.GetArrayLength() > 0 &&
                items[0].GetProperty("contentDetails").GetProperty("relatedPlaylists").TryGetProperty("likes", out var likes) &&
                likes.GetString() is { Length: > 0 } likesId && list.All(p => p.Id != likesId))
            {
                var liked = await GetPlaylistAsync(likesId, ct).ConfigureAwait(false);
                if (liked is not null) list.Insert(0, liked with { Title = "Vidéos « J'aime »" });
            }
        }
        catch (Application.Errors.ApiException ex)
        {
            _logger.LogDebug("Playlist J'aime indisponible : {Message}", ex.Message);
        }
        return list;
    }

    public async Task<RemotePlaylist?> GetPlaylistAsync(string playlistId, CancellationToken ct)
    {
        using var doc = await GetAsync($"playlists?part=snippet,contentDetails&id={Uri.EscapeDataString(playlistId)}&maxResults=1", requiresUser: false, ct).ConfigureAwait(false);
        var items = doc.RootElement.GetProperty("items");
        return items.GetArrayLength() == 0 ? null : MapPlaylist(items[0]);
    }

    public async IAsyncEnumerable<RemotePlaylistItem> GetPlaylistItemsAsync(string playlistId, [EnumeratorCancellation] CancellationToken ct)
    {
        string? pageToken = null;
        var index = 0;
        do
        {
            var url = $"playlistItems?part=snippet,contentDetails,status&playlistId={Uri.EscapeDataString(playlistId)}&maxResults={PageSize}" + PageParam(pageToken);
            List<(string ItemId, string VideoId, int Position, string Title, string? Channel, string? Thumb, string? Privacy)> page = new();
            using (var doc = await GetAsync(url, requiresUser: false, ct).ConfigureAwait(false))
            {
                foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
                {
                    var snippet = item.GetProperty("snippet");
                    var videoId = item.TryGetProperty("contentDetails", out var cd) && cd.TryGetProperty("videoId", out var v)
                        ? v.GetString() ?? string.Empty
                        : snippet.TryGetProperty("resourceId", out var rid) && rid.TryGetProperty("videoId", out var v2) ? v2.GetString() ?? string.Empty : string.Empty;
                    var position = snippet.TryGetProperty("position", out var pos) && pos.TryGetInt32(out var p) ? p : index;
                    var owner = snippet.TryGetProperty("videoOwnerChannelTitle", out var oc) ? oc.GetString() : null;
                    var privacy = item.TryGetProperty("status", out var st) && st.TryGetProperty("privacyStatus", out var ps) ? ps.GetString() : null;
                    page.Add((item.GetProperty("id").GetString()!, videoId, position, snippet.GetProperty("title").GetString() ?? string.Empty,
                        owner, Thumbnail(snippet), privacy));
                    index++;
                }
                pageToken = NextPage(doc);
            }

            var details = await GetVideoDetailsAsync(page.Select(p => p.VideoId).Where(id => id.Length > 0).Distinct().ToList(), ct).ConfigureAwait(false);
            foreach (var p in page.OrderBy(p => p.Position))
            {
                details.TryGetValue(p.VideoId, out var d);
                var availability = Availability(p.Title, p.Privacy, d.Exists, d.Embeddable);
                yield return new RemotePlaylistItem(p.ItemId, p.VideoId, p.Position, p.Title, p.Channel ?? d.Channel, p.Thumb,
                    availability, d.DurationMs);
            }
        } while (pageToken is not null);
    }

    private static SourceAvailability Availability(string title, string? privacy, bool exists, bool embeddable)
    {
        if (title is "Deleted video" || privacy is "privacyStatusUnspecified" && !exists) return SourceAvailability.Deleted;
        if (title is "Private video" || privacy is "private") return SourceAvailability.Private;
        if (!exists) return SourceAvailability.Unavailable;
        return SourceAvailability.Available;
    }

    private async Task<Dictionary<string, (bool Exists, long? DurationMs, string? Channel, bool Embeddable)>> GetVideoDetailsAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        var result = new Dictionary<string, (bool, long?, string?, bool)>(StringComparer.Ordinal);
        foreach (var chunk in ids.Chunk(PageSize))
        {
            var url = $"videos?part=contentDetails,snippet,status&id={string.Join(',', chunk.Select(Uri.EscapeDataString))}&maxResults={PageSize}";
            using var doc = await GetAsync(url, requiresUser: false, ct).ConfigureAwait(false);
            foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                var id = item.GetProperty("id").GetString()!;
                long? duration = null;
                if (item.TryGetProperty("contentDetails", out var cd) && cd.TryGetProperty("duration", out var dur) && dur.GetString() is { } iso)
                {
                    try { duration = (long)XmlConvert.ToTimeSpan(iso).TotalMilliseconds; } catch (FormatException) { }
                }
                var channel = item.TryGetProperty("snippet", out var sn) && sn.TryGetProperty("channelTitle", out var ch) ? ch.GetString() : null;
                var embeddable = !item.TryGetProperty("status", out var st) || !st.TryGetProperty("embeddable", out var em) || em.GetBoolean();
                result[id] = (true, duration, channel, embeddable);
            }
        }
        return result;
    }

    private static RemotePlaylist MapPlaylist(JsonElement item)
    {
        var snippet = item.GetProperty("snippet");
        var count = item.TryGetProperty("contentDetails", out var cd) && cd.TryGetProperty("itemCount", out var c) && c.TryGetInt32(out var n) ? n : 0;
        return new RemotePlaylist(
            item.GetProperty("id").GetString()!,
            snippet.GetProperty("title").GetString() ?? "(sans titre)",
            snippet.TryGetProperty("description", out var d) ? d.GetString() : null,
            Thumbnail(snippet),
            count,
            snippet.TryGetProperty("channelTitle", out var ch) ? ch.GetString() : null);
    }

    private static string? Thumbnail(JsonElement snippet)
    {
        if (!snippet.TryGetProperty("thumbnails", out var thumbs) || thumbs.ValueKind != JsonValueKind.Object) return null;
        foreach (var size in new[] { "medium", "high", "standard", "default", "maxres" })
        {
            if (thumbs.TryGetProperty(size, out var t) && t.TryGetProperty("url", out var u)) return u.GetString();
        }
        return null;
    }

    private static string PageParam(string? token) => token is null ? string.Empty : "&pageToken=" + Uri.EscapeDataString(token);

    private static string? NextPage(JsonDocument doc) =>
        doc.RootElement.TryGetProperty("nextPageToken", out var t) && t.GetString() is { Length: > 0 } s ? s : null;

    private async Task<JsonDocument> GetAsync(string relativeUrl, bool requiresUser, CancellationToken ct)
    {
        var client = _factory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        request.Options.Set(new HttpRequestOptionsKey<bool>(YouTubeAuthHandler.RequiresUserOption), requiresUser);
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw await ApiErrors.FromGoogleAsync(response, ct).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }
}

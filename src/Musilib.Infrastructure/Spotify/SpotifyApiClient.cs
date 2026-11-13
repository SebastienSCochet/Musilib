using System.Net.Http.Json;
using System.Text.Json;
using Musilib.Application.Abstractions;
using Musilib.Application.Settings;
using Musilib.Domain.Entities;
using Musilib.Infrastructure.Http;

namespace Musilib.Infrastructure.Spotify;

/// <summary>
/// Client Spotify Web API. Utilise les endpoints en vigueur depuis février 2026 :
/// POST /me/playlists, POST /playlists/{id}/items, GET /playlists/{id}/items.
/// La recherche est limitée à 10 résultats par requête pour les applications en mode développement.
/// </summary>
public sealed class SpotifyApiClient : ISpotifyClient
{
    public const string HttpClientName = "spotify";

    private readonly IHttpClientFactory _factory;
    private readonly ISettingsService _settings;

    public SpotifyApiClient(IHttpClientFactory factory, ISettingsService settings)
    {
        _factory = factory;
        _settings = settings;
    }

    private SpotifySettings Settings => _settings.Current.Spotify;

    public async Task<IReadOnlyList<SpotifyCandidate>> SearchTracksAsync(string query, int limit, int offset, CancellationToken ct)
    {
        var url = $"search?type=track&q={Uri.EscapeDataString(query)}&limit={Math.Clamp(limit, 1, 50)}&offset={Math.Max(0, offset)}{MarketParam()}";
        using var doc = await SendAsync(HttpMethod.Get, url, null, ct).ConfigureAwait(false);
        var list = new List<SpotifyCandidate>();
        if (doc.RootElement.TryGetProperty("tracks", out var tracks) && tracks.TryGetProperty("items", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object) list.Add(MapTrack(item));
            }
        }
        return list;
    }

    public async Task<SpotifyCandidate?> GetTrackAsync(string trackId, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, $"tracks/{Uri.EscapeDataString(trackId)}?{MarketParam().TrimStart('&')}", null, ct).ConfigureAwait(false);
        return MapTrack(doc.RootElement);
    }

    public async Task<SpotifyUser> GetCurrentUserAsync(CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, "me", null, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        var id = root.GetProperty("id").GetString()!;
        var name = root.TryGetProperty("display_name", out var dn) && dn.ValueKind == JsonValueKind.String ? dn.GetString()! : id;
        return new SpotifyUser(id, name, FirstImage(root));
    }

    public async Task<IReadOnlyList<SpotifyPlaylistInfo>> GetMyPlaylistsAsync(CancellationToken ct)
    {
        var list = new List<SpotifyPlaylistInfo>();
        string? next = "me/playlists?limit=50&offset=0";
        while (next is not null)
        {
            using var doc = await SendAsync(HttpMethod.Get, next, null, ct).ConfigureAwait(false);
            foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object) list.Add(MapPlaylist(item));
            }
            next = doc.RootElement.TryGetProperty("next", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        }
        return list;
    }

    public async Task<SpotifyPlaylistInfo> CreatePlaylistAsync(string name, string? description, bool isPublic, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["public"] = isPublic,
            ["description"] = string.IsNullOrWhiteSpace(description) ? "Créée avec Musilib Manager" : description
        };
        using var doc = await SendAsync(HttpMethod.Post, "me/playlists", JsonContent.Create(body), ct).ConfigureAwait(false);
        return MapPlaylist(doc.RootElement);
    }

    public async Task AddItemsAsync(string playlistId, IReadOnlyList<string> uris, CancellationToken ct)
    {
        if (uris.Count == 0) return;
        if (uris.Count > ISpotifyClient.MaxItemsPerAddRequest)
            throw new ArgumentOutOfRangeException(nameof(uris), $"Maximum {ISpotifyClient.MaxItemsPerAddRequest} éléments par requête.");
        var body = new Dictionary<string, object> { ["uris"] = uris };
        using var _ = await SendAsync(HttpMethod.Post, $"playlists/{Uri.EscapeDataString(playlistId)}/items", JsonContent.Create(body), ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> GetPlaylistItemUrisAsync(string playlistId, CancellationToken ct)
    {
        var list = new List<string>();
        string? next = $"playlists/{Uri.EscapeDataString(playlistId)}/items?limit=50&offset=0";
        while (next is not null)
        {
            using var doc = await SendAsync(HttpMethod.Get, next, null, ct).ConfigureAwait(false);
            foreach (var entry in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                // Nouveau format : "item" ; format historique : "track".
                if ((entry.TryGetProperty("item", out var obj) || entry.TryGetProperty("track", out obj))
                    && obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty("uri", out var uri) && uri.GetString() is { } u)
                    list.Add(u);
            }
            next = doc.RootElement.TryGetProperty("next", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        }
        return list;
    }

    private string MarketParam() => string.IsNullOrWhiteSpace(Settings.Market) ? string.Empty : "&market=" + Uri.EscapeDataString(Settings.Market);

    internal static SpotifyCandidate MapTrack(JsonElement t)
    {
        var album = t.TryGetProperty("album", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
        return new SpotifyCandidate
        {
            SpotifyId = t.GetProperty("id").GetString() ?? string.Empty,
            Uri = t.GetProperty("uri").GetString() ?? string.Empty,
            Title = t.GetProperty("name").GetString() ?? string.Empty,
            Artists = Names(t, "artists"),
            AlbumArtists = album.ValueKind == JsonValueKind.Object ? Names(album, "artists") : new List<string>(),
            Album = album.ValueKind == JsonValueKind.Object && album.TryGetProperty("name", out var an) ? an.GetString() : null,
            AlbumId = album.ValueKind == JsonValueKind.Object && album.TryGetProperty("id", out var ai) ? ai.GetString() : null,
            ReleaseDate = album.ValueKind == JsonValueKind.Object && album.TryGetProperty("release_date", out var rd) ? rd.GetString() : null,
            CoverUrl = album.ValueKind == JsonValueKind.Object ? FirstImage(album) : null,
            DurationMs = t.TryGetProperty("duration_ms", out var d) && d.TryGetInt64(out var ms) ? ms : 0,
            Isrc = t.TryGetProperty("external_ids", out var ext) && ext.ValueKind == JsonValueKind.Object && ext.TryGetProperty("isrc", out var isrc) ? isrc.GetString() : null,
            PreviewUrl = t.TryGetProperty("preview_url", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null,
            ExternalUrl = t.TryGetProperty("external_urls", out var eu) && eu.TryGetProperty("spotify", out var s) ? s.GetString() : null,
            Explicit = t.TryGetProperty("explicit", out var e) && e.ValueKind == JsonValueKind.True,
            TrackNumber = t.TryGetProperty("track_number", out var tn) && tn.TryGetInt32(out var tni) ? tni : null,
            DiscNumber = t.TryGetProperty("disc_number", out var dn) && dn.TryGetInt32(out var dni) ? dni : null
        };
    }

    private static SpotifyPlaylistInfo MapPlaylist(JsonElement p)
    {
        var owner = p.TryGetProperty("owner", out var o) ? o : default;
        var total = 0;
        foreach (var field in new[] { "items", "tracks" })
        {
            if (p.TryGetProperty(field, out var t) && t.ValueKind == JsonValueKind.Object && t.TryGetProperty("total", out var tt) && tt.TryGetInt32(out total))
                break;
        }
        return new SpotifyPlaylistInfo(
            p.GetProperty("id").GetString()!,
            p.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty,
            FirstImage(p),
            total,
            owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty("id", out var oid) ? oid.GetString() ?? string.Empty : string.Empty,
            owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty("display_name", out var od) && od.ValueKind == JsonValueKind.String ? od.GetString() : null,
            p.TryGetProperty("collaborative", out var c) && c.ValueKind == JsonValueKind.True,
            p.TryGetProperty("public", out var pub) && pub.ValueKind == JsonValueKind.True,
            p.TryGetProperty("external_urls", out var eu) && eu.TryGetProperty("spotify", out var s) ? s.GetString() : null);
    }

    private static List<string> Names(JsonElement element, string property)
    {
        var list = new List<string>();
        if (element.TryGetProperty(property, out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in arr.EnumerateArray())
            {
                if (a.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name) list.Add(name);
            }
        }
        return list;
    }

    private static string? FirstImage(JsonElement element)
    {
        if (!element.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array) return null;
        string? best = null;
        var bestWidth = 0;
        foreach (var img in images.EnumerateArray())
        {
            var w = img.TryGetProperty("width", out var wi) && wi.TryGetInt32(out var wv) ? wv : 300;
            var url = img.TryGetProperty("url", out var u) ? u.GetString() : null;
            // On privilégie une image d'environ 300 px (affichage) sans dépasser 640 px.
            if (url is not null && (best is null || (w <= 640 && w > bestWidth))) { best = url; bestWidth = w; }
        }
        return best;
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string url, HttpContent? content, CancellationToken ct)
    {
        var client = _factory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(method, url) { Content = content };
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw await ApiErrors.FromSpotifyAsync(response, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
    }
}

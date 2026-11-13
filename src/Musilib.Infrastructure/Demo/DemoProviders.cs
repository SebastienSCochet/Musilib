using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Musilib.Application.Abstractions;
using Musilib.Domain.Entities;
using Musilib.Domain.Enums;
using Musilib.Domain.Services;

namespace Musilib.Infrastructure.Demo;

/// <summary>Fournisseur YouTube fictif (mode démonstration, aucun appel réseau).</summary>
public sealed class DemoYouTubeClient : IYouTubeClient
{
    public Task<string?> GetChannelTitleAsync(CancellationToken ct) => Task.FromResult<string?>("Chaîne de démonstration");

    public async Task<IReadOnlyList<RemotePlaylist>> GetMyPlaylistsAsync(CancellationToken ct)
    {
        await Task.Delay(250, ct).ConfigureAwait(false);
        return DemoCatalog.Playlists.Select(Map).ToList();
    }

    public Task<RemotePlaylist?> GetPlaylistAsync(string playlistId, CancellationToken ct)
    {
        var p = DemoCatalog.Playlists.FirstOrDefault(x => x.Id == playlistId);
        return Task.FromResult(p is null ? null : Map(p));
    }

    public async IAsyncEnumerable<RemotePlaylistItem> GetPlaylistItemsAsync(string playlistId, [EnumeratorCancellation] CancellationToken ct)
    {
        var p = DemoCatalog.Playlists.FirstOrDefault(x => x.Id == playlistId)
                ?? throw new InvalidOperationException("Playlist de démonstration introuvable.");
        var position = 0;
        foreach (var item in p.Items)
        {
            await Task.Delay(60, ct).ConfigureAwait(false);
            var song = DemoCatalog.FindSong(item.SongKey);
            long? duration = item.Availability == SourceAvailability.Available && song is not null
                ? (song.DurationSec + item.DurationOffsetSec) * 1000L
                : null;
            yield return new RemotePlaylistItem(
                $"{playlistId}-{position}",
                DemoCatalog.VideoId(playlistId + item.SongKey + position),
                position,
                item.VideoTitle,
                string.IsNullOrEmpty(item.Channel) ? null : item.Channel,
                null,
                item.Availability,
                duration);
            position++;
        }
    }

    private static RemotePlaylist Map(DemoCatalog.DemoPlaylist p) => new(p.Id, p.Title, p.Description, null, p.Items.Count, "Chaîne de démonstration");
}

/// <summary>Fournisseur Spotify fictif : recherche locale dans le catalogue, playlists en mémoire.</summary>
public sealed partial class DemoSpotifyClient : ISpotifyClient
{
    private readonly List<SpotifyCandidate> _catalog;
    private readonly ConcurrentDictionary<string, (SpotifyPlaylistInfo Info, List<string> Uris)> _playlists = new();
    private readonly string _cacheDirectory;

    public DemoSpotifyClient(IAppPaths paths)
    {
        _cacheDirectory = paths.CacheDirectory;
        _catalog = BuildCatalog();
        foreach (var (name, count) in new[] { ("Favoris 2025", 42), ("Running", 18) })
        {
            var id = DemoCatalog.SpotifyId("pl:" + name);
            _playlists[id] = (new SpotifyPlaylistInfo(id, name, null, count, "demo-user", "Utilisateur démo", false, false,
                $"https://open.spotify.com/playlist/{id}"), new List<string>());
        }
    }

    public int SearchCalls;

    private List<SpotifyCandidate> BuildCatalog()
    {
        var list = new List<SpotifyCandidate>();
        var index = 0;
        foreach (var s in DemoCatalog.Songs.Where(s => s.OnSpotify))
        {
            var artists = new List<string> { s.Artist };
            if (s.Featuring is not null) artists.Add(s.Featuring);
            list.Add(Candidate(s, s.Title, s.Album, s.ReleaseDate, s.DurationSec, artists, "album", index++, s.Key));
            if (s.HasSingle) list.Add(Candidate(s, s.Title, s.Title, s.ReleaseDate[..4] + "-01-20", s.DurationSec, artists, "single", index++, s.Key));
            if (s.HasLive) list.Add(Candidate(s, s.Title + " - Live", s.Album + " (Live Sessions)", s.ReleaseDate[..4] + "-12-01", s.DurationSec + 31, artists, "live", index++, s.Key + "-live"));
            if (s.HasRemix) list.Add(Candidate(s, s.Title + " - Static Gardens Remix", s.Title + " (Remixes)", s.ReleaseDate[..4] + "-09-09", s.DurationSec + 54, new List<string>(artists) { "Static Gardens" }, "remix", index++, s.Key + "-remix"));
        }
        // Homonymes et reprises pour rendre la recherche réaliste.
        list.Add(new SpotifyCandidate
        {
            SpotifyId = DemoCatalog.SpotifyId("cover-static"), Uri = "spotify:track:" + DemoCatalog.SpotifyId("cover-static"),
            Title = "Midnight Static", Artists = ["Harbor Lights Tribute Band"], AlbumArtists = ["Harbor Lights Tribute Band"],
            Album = "Synthwave Covers Vol. 2", ReleaseDate = "2025-05-05", DurationMs = 219_000,
            ExternalUrl = "https://open.spotify.com/track/" + DemoCatalog.SpotifyId("cover-static")
        });
        return list;
    }

    private SpotifyCandidate Candidate(DemoCatalog.Song s, string title, string album, string date, int duration, List<string> artists, string kind, int index, string isrcKey)
    {
        var id = DemoCatalog.SpotifyId(s.Key + ":" + kind);
        return new SpotifyCandidate
        {
            SpotifyId = id,
            Uri = "spotify:track:" + id,
            Title = title,
            Artists = artists,
            AlbumArtists = [s.Artist],
            Album = album,
            AlbumId = DemoCatalog.SpotifyId("album:" + album),
            ReleaseDate = date,
            DurationMs = duration * 1000L,
            Isrc = DemoCatalog.Isrc(isrcKey),
            CoverUrl = null,
            PreviewUrl = index % 4 == 3 ? null : DemoAudio.EnsurePreview(_cacheDirectory, index),
            ExternalUrl = "https://open.spotify.com/track/" + id,
            TrackNumber = 1 + index % 12,
            DiscNumber = 1
        };
    }

    public async Task<IReadOnlyList<SpotifyCandidate>> SearchTracksAsync(string query, int limit, int offset, CancellationToken ct)
    {
        Interlocked.Increment(ref SearchCalls);
        await Task.Delay(Random.Shared.Next(60, 160), ct).ConfigureAwait(false);

        string? trackFilter = null, artistFilter = null, isrcFilter = null;
        var free = FilterPattern().Replace(query, m =>
        {
            var value = m.Groups["v"].Value.Trim('"');
            switch (m.Groups["f"].Value.ToLowerInvariant())
            {
                case "track": trackFilter = value; break;
                case "artist": artistFilter = value; break;
                case "isrc": isrcFilter = value; break;
            }
            return " ";
        });

        var freeTokens = TextNormalizer.Tokens(free).ToHashSet();
        var results = new List<(double Score, SpotifyCandidate C)>();
        foreach (var c in _catalog)
        {
            if (isrcFilter is not null)
            {
                if (string.Equals(c.Isrc, isrcFilter, StringComparison.OrdinalIgnoreCase)) results.Add((10, c));
                continue;
            }
            double score = 0;
            if (trackFilter is not null)
            {
                var sim = StringSimilarity.TokenSetRatio(trackFilter, c.Title);
                if (sim < 0.55) continue;
                score += sim;
            }
            if (artistFilter is not null)
            {
                var sim = c.Artists.Max(a => StringSimilarity.TokenSetRatio(artistFilter, a));
                if (sim < 0.6) continue;
                score += sim;
            }
            if (freeTokens.Count > 0)
            {
                var tokens = TextNormalizer.Tokens(c.Title + " " + string.Join(' ', c.Artists) + " " + c.Album).ToHashSet();
                var overlap = freeTokens.Count(tokens.Contains);
                if (overlap == 0) continue;
                score += (double)overlap / freeTokens.Count;
            }
            if (score > 0) results.Add((score, c));
        }
        return results.OrderByDescending(r => r.Score).Skip(offset).Take(limit).Select(r => r.C).ToList();
    }

    public Task<SpotifyCandidate?> GetTrackAsync(string trackId, CancellationToken ct) =>
        Task.FromResult(_catalog.FirstOrDefault(c => c.SpotifyId == trackId));

    public Task<SpotifyUser> GetCurrentUserAsync(CancellationToken ct) => Task.FromResult(new SpotifyUser("demo-user", "Utilisateur démo", null));

    public Task<IReadOnlyList<SpotifyPlaylistInfo>> GetMyPlaylistsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SpotifyPlaylistInfo>>(_playlists.Values.Select(p => p.Info with { TrackCount = Math.Max(p.Info.TrackCount, p.Uris.Count) }).ToList());

    public async Task<SpotifyPlaylistInfo> CreatePlaylistAsync(string name, string? description, bool isPublic, CancellationToken ct)
    {
        await Task.Delay(200, ct).ConfigureAwait(false);
        var id = DemoCatalog.SpotifyId("pl:" + name + Guid.NewGuid());
        var info = new SpotifyPlaylistInfo(id, name, null, 0, "demo-user", "Utilisateur démo", false, isPublic, $"https://open.spotify.com/playlist/{id}");
        _playlists[id] = (info, new List<string>());
        return info;
    }

    public async Task AddItemsAsync(string playlistId, IReadOnlyList<string> uris, CancellationToken ct)
    {
        if (uris.Count > ISpotifyClient.MaxItemsPerAddRequest) throw new ArgumentOutOfRangeException(nameof(uris));
        await Task.Delay(300, ct).ConfigureAwait(false);
        if (!_playlists.TryGetValue(playlistId, out var p))
            throw new Application.Errors.ApiException("Spotify", "Playlist introuvable", System.Net.HttpStatusCode.NotFound);
        lock (p.Uris) p.Uris.AddRange(uris);
    }

    public Task<IReadOnlyList<string>> GetPlaylistItemUrisAsync(string playlistId, CancellationToken ct)
    {
        if (!_playlists.TryGetValue(playlistId, out var p)) return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        lock (p.Uris) return Task.FromResult<IReadOnlyList<string>>(p.Uris.ToList());
    }

    [GeneratedRegex(@"(?<f>track|artist|isrc|album):(?<v>""[^""]*""|\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex FilterPattern();
}

/// <summary>Authentification simulée : toujours connecté en mode démonstration.</summary>
public sealed class DemoAuthService : IAuthService
{
    public DemoAuthService(ExternalService provider) => Provider = provider;

    public ExternalService Provider { get; }

    public Task<ConnectionInfo> GetStatusAsync(CancellationToken ct) => Task.FromResult(Info());

    public async Task<ConnectionInfo> SignInAsync(CancellationToken ct)
    {
        await Task.Delay(400, ct).ConfigureAwait(false);
        return Info();
    }

    public Task SignOutAsync(CancellationToken ct) => Task.CompletedTask;
    public Task<string> GetAccessTokenAsync(CancellationToken ct) => Task.FromResult("demo-token");
    public Task<string> RefreshAccessTokenAsync(CancellationToken ct) => Task.FromResult("demo-token");
    public Task SetAccountNameAsync(string? accountName, CancellationToken ct) => Task.CompletedTask;

    private ConnectionInfo Info() => new(Provider, true, true,
        Provider == ExternalService.Spotify ? "Utilisateur démo" : "Chaîne de démonstration",
        DateTimeOffset.UtcNow.AddHours(1), null, IsDemo: true);
}

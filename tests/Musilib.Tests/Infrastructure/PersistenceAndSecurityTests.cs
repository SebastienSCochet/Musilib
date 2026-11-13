using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Musilib.Application.Abstractions;
using Musilib.Application.Common;
using Musilib.Application.Errors;
using Musilib.Domain.Entities;
using Musilib.Domain.Enums;
using Musilib.Infrastructure.Auth;
using Musilib.Infrastructure.Demo;
using Musilib.Infrastructure.Logging;
using Musilib.Infrastructure.Persistence;
using Musilib.Infrastructure.Security;
using Musilib.Tests.Helpers;

namespace Musilib.Tests.Infrastructure;

public class PersistenceTests
{
    [Fact]
    public async Task Schema_initialization_is_idempotent()
    {
        await using var db = await TempDatabase.CreateAsync();
        var init = new DatabaseInitializer(db.Factory, NullLogger<DatabaseInitializer>.Instance);
        await init.InitializeAsync();
        await init.InitializeAsync();
        await using var c = await db.Factory.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM SchemaVersion";
        Assert.Equal((long)DatabaseInitializer.LatestVersion, (long)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Track_roundtrip_preserves_every_field()
    {
        await using var db = await TempDatabase.CreateAsync();
        var (p, _) = await db.SeedPlaylistAsync(0);
        var track = new Track
        {
            PlaylistId = p.Id, Position = 7, SourceItemId = "it", SourceVideoId = "v", SourceTitle = "Src", SourceChannel = "Ch",
            SourceDurationMs = 123, SourceThumbnailUrl = "thumb", Availability = SourceAvailability.Private, RemovedFromSource = true,
            Metadata = new TrackMetadata
            {
                Title = "T", Artists = ["A", "B"], Album = "Al", AlbumArtists = ["AA"], ReleaseDate = "2020-02-02", DurationMs = 456,
                Isrc = "I", CoverUrl = "c", CoverLocalPath = "cl", Genre = "G", TrackNumber = 3, DiscNumber = 2, Comment = "Co"
            },
            MetadataOrigin = MetadataOrigin.Manual, MetadataEdited = true, MatchStatus = MatchStatus.Confirmed, MatchScore = 0.91,
            SpotifyTrackId = "sid", SpotifyUri = "suri", SpotifyUrl = "surl", PreviewUrl = "prev", CustomSearchQuery = "q",
            LocalFilePath = "lf", Excluded = true, LastError = "err"
        };
        await db.Tracks.UpdateAsync(track);

        var loaded = await db.Tracks.GetAsync(track.Id);
        Assert.Equal(JsonDefaults.Serialize(track), JsonDefaults.Serialize(loaded));
    }

    [Fact]
    public async Task Stats_count_statuses_per_playlist()
    {
        await using var db = await TempDatabase.CreateAsync();
        var (p, _) = await db.SeedPlaylistAsync(6, (t, i) =>
        {
            t.MatchStatus = (MatchStatus)(i % 6);
            if (i == 5) t.Excluded = true;
            if (i == 4) t.Availability = SourceAvailability.Deleted;
            if (i == 3) t.LocalFilePath = "x.mp3";
        });
        await db.SeedPlaylistAsync(2);

        var stats = await db.Tracks.GetStatsAsync(p.Id);
        Assert.Equal(6, stats.Total);
        Assert.Equal(1, stats.Pending);
        Assert.Equal(1, stats.AutoMatched);
        Assert.Equal(1, stats.Ambiguous);
        Assert.Equal(1, stats.Confirmed);
        Assert.Equal(1, stats.Excluded);
        Assert.Equal(1, stats.Unavailable);
        Assert.Equal(1, stats.WithLocalFile);
        Assert.Equal(8, (await db.Tracks.GetStatsAsync(null)).Total);
    }

    [Fact]
    public async Task Deleting_playlist_cascades_to_tracks_and_candidates()
    {
        await using var db = await TempDatabase.CreateAsync();
        var (p, tracks) = await db.SeedPlaylistAsync(2);
        await db.Tracks.SaveCandidatesAsync(tracks[0].Id, [Candidates.Make("a", "x", "y", 1)]);
        await db.Playlists.DeleteAsync(p.Id);
        Assert.Empty(await db.Tracks.GetByPlaylistAsync(p.Id));
        Assert.Empty(await db.Tracks.GetCandidatesAsync(tracks[0].Id));
    }

    [Fact]
    public async Task Playlist_lookup_by_external_id_and_stats()
    {
        await using var db = await TempDatabase.CreateAsync();
        var (p, _) = await db.SeedPlaylistAsync(3);
        var found = await db.Playlists.FindByExternalIdAsync(PlaylistSource.YouTube, p.ExternalId);
        Assert.Equal(p.Id, found!.Id);
        Assert.Equal(3, found.Stats.Total);
        Assert.Null(await db.Playlists.FindByExternalIdAsync(PlaylistSource.Demo, p.ExternalId));
    }

    [Fact]
    public async Task Jobs_and_items_are_persisted()
    {
        await using var db = await TempDatabase.CreateAsync();
        var job = new Job { Type = JobType.Transfer, Title = "t", Payload = "{}", Total = 10 };
        await db.Jobs.InsertAsync(job);
        job.Status = JobStatus.Running;
        job.Processed = 4;
        job.State = "{\"a\":1}";
        await db.Jobs.UpdateAsync(job);
        await db.Jobs.UpsertItemAsync(new JobItem { JobId = job.Id, ItemKey = "k", Status = JobItemStatus.Failed, Error = "e" });
        await db.Jobs.UpsertItemAsync(new JobItem { JobId = job.Id, ItemKey = "k", Status = JobItemStatus.Completed, Attempts = 2 });

        var loaded = await db.Jobs.GetAsync(job.Id);
        Assert.Equal(JobStatus.Running, loaded!.Status);
        Assert.Equal(4, loaded.Processed);
        Assert.Equal("{\"a\":1}", loaded.State);
        var item = Assert.Single(await db.Jobs.GetItemsAsync(job.Id));
        Assert.Equal(JobItemStatus.Completed, item.Status);
        Assert.Single(await db.Jobs.GetByStatusAsync([JobStatus.Running]));

        job.Status = JobStatus.Completed;
        await db.Jobs.UpdateAsync(job);
        Assert.Equal(1, await db.Jobs.DeleteTerminatedAsync());
        Assert.Empty(await db.Jobs.GetItemsAsync(job.Id));
    }

    [Fact]
    public async Task Search_cache_honours_max_age()
    {
        await using var db = await TempDatabase.CreateAsync();
        await db.Cache.SetAsync("k", [Candidates.Make("a", "t", "a", 1)]);
        Assert.Single((await db.Cache.TryGetAsync("k", TimeSpan.FromDays(1)))!);
        Assert.Null(await db.Cache.TryGetAsync("k", TimeSpan.Zero));
        Assert.Null(await db.Cache.TryGetAsync("missing", TimeSpan.FromDays(1)));
        await db.Cache.ClearAsync();
        Assert.Equal(0, await db.Cache.CountAsync());
    }

    [Fact]
    public async Task History_filters_and_counts()
    {
        await using var db = await TempDatabase.CreateAsync();
        await db.History.AddAsync(new HistoryEntry { Kind = HistoryKind.Transfer, Title = "ok" });
        await db.History.AddAsync(new HistoryEntry { Kind = HistoryKind.Transfer, Title = "ko", Success = false });
        await db.History.AddAsync(new HistoryEntry { Kind = HistoryKind.Import, Title = "imp" });
        Assert.Equal(1, await db.History.CountAsync(HistoryKind.Transfer, successOnly: true));
        Assert.Equal(2, (await db.History.GetRecentAsync(10, HistoryKind.Transfer)).Count);
        Assert.Equal("imp", (await db.History.GetRecentAsync(1)).Single().Title);
    }

    [Fact]
    public async Task Data_survives_reopening_the_database()
    {
        var dir = Path.Combine(Path.GetTempPath(), "musilib-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "persist.db");
        try
        {
            var f1 = new SqliteConnectionFactory(path);
            await new DatabaseInitializer(f1, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
            var tracks1 = new TrackRepository(f1);
            var playlist = new Playlist { ExternalId = "X", Title = "Persist" };
            await new PlaylistRepository(f1, tracks1).UpsertAsync(playlist);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            var f2 = new SqliteConnectionFactory(path);
            await new DatabaseInitializer(f2, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
            var again = await new PlaylistRepository(f2, new TrackRepository(f2)).GetAsync(playlist.Id);
            Assert.Equal("Persist", again!.Title);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}

public class SecurityTests
{
    [Fact]
    public async Task Secure_store_encrypts_and_roundtrips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "musilib-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ProtectedFileStore(dir);
            await store.SetAsync("secret.key", "super-secret-value");
            Assert.Equal("super-secret-value", await store.GetAsync("secret.key"));
            foreach (var file in Directory.GetFiles(dir, "*.bin"))
                Assert.DoesNotContain("super-secret-value", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)));
            await store.DeleteAsync("secret.key");
            Assert.Null(await store.GetAsync("secret.key"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Theory]
    [InlineData("Authorization: Bearer abc.def-123", "Authorization: Bearer ***")]
    [InlineData("POST /token?code=4/0Adeu5&state=x", "POST /token?code=***&state=x")]
    [InlineData("{\"access_token\":\"ya29.secret\",\"expires_in\":3599}", "{\"access_token\":\"***\",\"expires_in\":3599}")]
    [InlineData("client_secret=GOCSPX-123&grant_type=refresh_token", "client_secret=***&grant_type=refresh_token")]
    [InlineData("nothing sensitive here", "nothing sensitive here")]
    public void Redactor_masks_credentials(string input, string expected) =>
        Assert.Equal(expected, SensitiveDataRedactor.Redact(input));

    [Fact]
    public void Pkce_challenge_matches_rfc7636_example()
    {
        // Exemple de la RFC 7636, annexe B.
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", Pkce.CreateChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
        var verifier = Pkce.CreateVerifier();
        Assert.InRange(verifier.Length, 43, 128);
        Assert.DoesNotContain('=', verifier);
    }
}

public class OAuthTests
{
    private sealed class TestOAuthService : OAuthServiceBase
    {
        public TestOAuthService(ISecureStore store, IHttpClientFactory http, TimeProvider time)
            : base(store, new NoBrowser(), http, NullLogger.Instance, time) { }

        public override ExternalService Provider => ExternalService.Spotify;
        protected override string TokenKey => "test.tokens";
        protected override string TokenEndpoint => "https://auth.test/token";
        protected override string CallbackPath => "/callback";
        protected override int RedirectPort => 0;
        protected override Task<bool> IsConfiguredAsync(CancellationToken ct) => Task.FromResult(true);
        protected override string BuildRedirectUri(int port) => $"http://127.0.0.1:{port}/callback";
        protected override string BuildAuthorizeUrl(string redirectUri, string state, string codeChallenge) => "https://auth.test/authorize";
        protected override Task<Dictionary<string, string>> BuildCodeExchangeAsync(string code, string redirectUri, string verifier, CancellationToken ct) =>
            Task.FromResult(new Dictionary<string, string> { ["code"] = code });
        protected override Task<Dictionary<string, string>> BuildRefreshAsync(string refreshToken, CancellationToken ct) =>
            Task.FromResult(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken });
    }

    private sealed class NoBrowser : IBrowserLauncher
    {
        public void Open(string url) { }
    }

    private sealed class MemoryStore : ISecureStore
    {
        public Dictionary<string, string> Values { get; } = new();
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult(Values.TryGetValue(key, out var v) ? v : null);
        public Task SetAsync(string key, string value, CancellationToken ct = default) { Values[key] = value; return Task.CompletedTask; }
        public Task DeleteAsync(string key, CancellationToken ct = default) { Values.Remove(key); return Task.CompletedTask; }
    }

    [Fact]
    public async Task Valid_token_is_returned_without_network()
    {
        var store = new MemoryStore();
        await store.SetAsync("test.tokens", JsonDefaults.Serialize(new TokenSet { AccessToken = "valid", RefreshToken = "r", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) }));
        var handler = new FakeHttpHandler(_ => throw new InvalidOperationException("no network expected"));
        var service = new TestOAuthService(store, new FakeHttpClientFactory(handler), TimeProvider.System);
        Assert.Equal("valid", await service.GetAccessTokenAsync(CancellationToken.None));
        Assert.True((await service.GetStatusAsync(CancellationToken.None)).IsConnected);
    }

    [Fact]
    public async Task Expired_token_is_refreshed_once_even_with_concurrent_callers()
    {
        var store = new MemoryStore();
        await store.SetAsync("test.tokens", JsonDefaults.Serialize(new TokenSet { AccessToken = "old", RefreshToken = "refresh-1", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5) }));
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{"access_token":"new","expires_in":3600,"token_type":"Bearer"}"""));
        var service = new TestOAuthService(store, new FakeHttpClientFactory(handler), TimeProvider.System);

        var tokens = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => service.GetAccessTokenAsync(CancellationToken.None)));

        Assert.All(tokens, t => Assert.Equal("new", t));
        Assert.Single(handler.Requests);
        Assert.Contains("refresh_token=refresh-1", handler.Requests[0].Body);
        var persisted = JsonDefaults.Deserialize<TokenSet>(store.Values["test.tokens"])!;
        Assert.Equal("refresh-1", persisted.RefreshToken);
    }

    [Fact]
    public async Task Revoked_refresh_token_requires_new_sign_in_and_clears_tokens()
    {
        var store = new MemoryStore();
        await store.SetAsync("test.tokens", JsonDefaults.Serialize(new TokenSet { AccessToken = "old", RefreshToken = "bad", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5) }));
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{"error":"invalid_grant","error_description":"Refresh token revoked"}""", HttpStatusCode.BadRequest));
        var service = new TestOAuthService(store, new FakeHttpClientFactory(handler), TimeProvider.System);

        await Assert.ThrowsAsync<AuthenticationRequiredException>(() => service.GetAccessTokenAsync(CancellationToken.None));
        Assert.False(store.Values.ContainsKey("test.tokens"));
    }

    [Fact]
    public async Task Not_connected_throws_authentication_required()
    {
        var service = new TestOAuthService(new MemoryStore(), new FakeHttpClientFactory(new FakeHttpHandler(_ => new HttpResponseMessage())), TimeProvider.System);
        await Assert.ThrowsAsync<AuthenticationRequiredException>(() => service.GetAccessTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Loopback_listener_receives_authorization_code()
    {
        using var listener = new LoopbackCallbackListener(0);
        var wait = listener.WaitForCallbackAsync("/callback", CancellationToken.None);

        using (var idle = new TcpClient())
        {
            // Connexion « spéculative » sans requête, comme en ouvrent certains navigateurs.
            await idle.ConnectAsync(IPAddress.Loopback, listener.Port);
        }
        using var http = new HttpClient();
        var favicon = await http.GetAsync($"http://127.0.0.1:{listener.Port}/favicon.ico");
        Assert.Equal(HttpStatusCode.NotFound, favicon.StatusCode);
        var response = await http.GetAsync($"http://127.0.0.1:{listener.Port}/callback?code=abc%2F123&state=xyz");

        var callback = await wait.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("abc/123", callback.Code);
        Assert.Equal("xyz", callback.State);
        Assert.Null(callback.Error);
    }
}

public class DemoProviderTests
{
    private sealed class Paths : IAppPaths
    {
        public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "musilib-tests-demo");
        public string CacheDirectory => Path.Combine(DataDirectory, "cache");
        public string LogDirectory => Path.Combine(DataDirectory, "logs");
    }

    [Fact]
    public async Task Demo_spotify_search_finds_catalog_tracks()
    {
        var client = new DemoSpotifyClient(new Paths());
        var results = await client.SearchTracksAsync("track:\"midnight static\" artist:\"neon harbor\"", 10, 0, CancellationToken.None);
        Assert.Contains(results, r => r.Title == "Midnight Static" && r.Artists.Contains("Neon Harbor"));
        Assert.Empty(await client.SearchTracksAsync("track:\"garage demo\" artist:\"basement tapes club\"", 10, 0, CancellationToken.None));
    }

    [Fact]
    public async Task Demo_youtube_lists_items_in_order()
    {
        var client = new DemoYouTubeClient();
        var playlists = await client.GetMyPlaylistsAsync(CancellationToken.None);
        Assert.NotEmpty(playlists);
        var items = new List<RemotePlaylistItem>();
        await foreach (var i in client.GetPlaylistItemsAsync(playlists[0].Id, CancellationToken.None)) items.Add(i);
        Assert.Equal(playlists[0].ItemCount, items.Count);
        Assert.Contains(items, i => i.Availability == SourceAvailability.Deleted);
        Assert.Equal(Enumerable.Range(0, items.Count), items.Select(i => i.Position));
    }
}

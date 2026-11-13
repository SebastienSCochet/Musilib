using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Musilib.Application.Abstractions;
using Musilib.Application.Errors;
using Musilib.Application.Settings;
using Musilib.Domain.Enums;
using Musilib.Infrastructure.Http;
using Musilib.Infrastructure.Spotify;
using Musilib.Infrastructure.YouTube;
using Musilib.Tests.Helpers;

namespace Musilib.Tests.Infrastructure;

public class RetryHandlerTests
{
    private static (HttpClient Client, List<TimeSpan> Delays, FakeHttpHandler Inner) Create(Func<int, HttpResponseMessage> responses, int maxRetries = 3, int maxRetryAfter = 60)
    {
        var delays = new List<TimeSpan>();
        var inner = new FakeHttpHandler((_, i) => Task.FromResult(responses(i)));
        var settings = new ProcessingSettings { MaxRetries = maxRetries, MaxRetryAfterSeconds = maxRetryAfter };
        var handler = new RetryHandler(() => settings, NullLogger.Instance, (d, _) => { delays.Add(d); return Task.CompletedTask; }) { InnerHandler = inner };
        return (new HttpClient(handler), delays, inner);
    }

    [Fact]
    public async Task Respects_retry_after_on_429()
    {
        var (client, delays, inner) = Create(i =>
        {
            if (i == 0)
            {
                var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
                return r;
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var response = await client.GetAsync("https://api.test/x");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(7)], delays);
    }

    [Fact]
    public async Task Uses_exponential_backoff_on_server_errors()
    {
        var (client, delays, inner) = Create(i => new HttpResponseMessage(i < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));

        var response = await client.GetAsync("https://api.test/x");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(4, inner.Requests.Count);
        Assert.Equal(3, delays.Count);
        Assert.True(delays[1] > delays[0] && delays[2] > delays[1], string.Join(",", delays));
    }

    [Fact]
    public async Task Gives_up_after_max_retries()
    {
        var (client, delays, inner) = Create(_ => new HttpResponseMessage(HttpStatusCode.BadGateway), maxRetries: 2);
        var response = await client.GetAsync("https://api.test/x");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(3, inner.Requests.Count);
        Assert.Equal(2, delays.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Does_not_retry_client_errors(HttpStatusCode status)
    {
        var (client, delays, inner) = Create(_ => new HttpResponseMessage(status));
        var response = await client.GetAsync("https://api.test/x");
        Assert.Equal(status, response.StatusCode);
        Assert.Single(inner.Requests);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Returns_429_when_retry_after_exceeds_limit()
    {
        var (client, delays, inner) = Create(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(2));
            return r;
        }, maxRetryAfter: 60);

        var response = await client.GetAsync("https://api.test/x");
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Single(inner.Requests);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Retries_network_failures()
    {
        var delays = new List<TimeSpan>();
        var inner = new FakeHttpHandler((_, i) => i < 2 ? throw new HttpRequestException("reset") : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var handler = new RetryHandler(() => new ProcessingSettings { MaxRetries = 3 }, NullLogger.Instance, (d, _) => { delays.Add(d); return Task.CompletedTask; }) { InnerHandler = inner };
        var response = await new HttpClient(handler).GetAsync("https://api.test/x");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, delays.Count);
    }
}

public class YouTubeApiClientTests
{
    private static string PlaylistItemsPage(int start, int count, string? next, params (int Index, string Title, string? Privacy)[] overrides)
    {
        var items = Enumerable.Range(start, count).Select(i =>
        {
            var o = overrides.FirstOrDefault(x => x.Index == i);
            return new
            {
                id = "item" + i,
                snippet = new
                {
                    title = o.Title ?? $"Artist {i} - Song {i}",
                    position = i,
                    videoOwnerChannelTitle = "Channel",
                    resourceId = new { videoId = "vid" + i },
                    thumbnails = new { medium = new { url = $"https://i.ytimg.com/{i}.jpg" } }
                },
                contentDetails = new { videoId = "vid" + i },
                status = new { privacyStatus = o.Privacy ?? "public" }
            };
        });
        return JsonSerializer.Serialize(new { nextPageToken = next, items });
    }

    private static string VideosResponse(IEnumerable<string> ids, params string[] missing) =>
        JsonSerializer.Serialize(new
        {
            items = ids.Where(id => !missing.Contains(id)).Select(id => new
            {
                id,
                contentDetails = new { duration = "PT3M25S" },
                snippet = new { channelTitle = "Channel" },
                status = new { embeddable = true }
            })
        });

    [Fact]
    public async Task Paginates_and_detects_unavailable_videos()
    {
        var handler = new FakeHttpHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("playlistItems") && !url.Contains("pageToken"))
                return FakeHttpHandler.Json(PlaylistItemsPage(0, 50, "PAGE2", (3, "Deleted video", "privacyStatusUnspecified"), (4, "Private video", "private")));
            if (url.Contains("playlistItems") && url.Contains("pageToken=PAGE2"))
                return FakeHttpHandler.Json(PlaylistItemsPage(50, 12, null));
            if (url.Contains("videos"))
            {
                var ids = Uri.UnescapeDataString(url.Split("id=")[1].Split('&')[0]).Split(',');
                return FakeHttpHandler.Json(VideosResponse(ids, "vid3", "vid4", "vid10"));
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var client = new YouTubeApiClient(new FakeHttpClientFactory(handler, "https://www.googleapis.com/youtube/v3/"), NullLogger<YouTubeApiClient>.Instance);

        var items = new List<RemotePlaylistItem>();
        await foreach (var i in client.GetPlaylistItemsAsync("PL1", CancellationToken.None)) items.Add(i);

        Assert.Equal(62, items.Count);
        Assert.Equal(Enumerable.Range(0, 62), items.Select(i => i.Position));
        Assert.Equal(SourceAvailability.Deleted, items[3].Availability);
        Assert.Equal(SourceAvailability.Private, items[4].Availability);
        Assert.Equal(SourceAvailability.Unavailable, items[10].Availability);
        Assert.Equal(205_000, items[0].DurationMs);
        Assert.Null(items[10].DurationMs);
        Assert.Equal(2, handler.Requests.Count(r => r.Url.Contains("playlistItems")));
        Assert.Equal(2, handler.Requests.Count(r => r.Url.Contains("videos")));
        Assert.All(handler.Requests, r => Assert.Contains("maxResults=50", r.Url));
    }

    [Fact]
    public async Task Quota_exceeded_raises_dedicated_exception()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(
            """{"error":{"code":403,"message":"The request cannot be completed because you have exceeded your quota.","errors":[{"reason":"quotaExceeded"}]}}""",
            HttpStatusCode.Forbidden));
        var client = new YouTubeApiClient(new FakeHttpClientFactory(handler, "https://www.googleapis.com/youtube/v3/"), NullLogger<YouTubeApiClient>.Instance);

        await Assert.ThrowsAsync<QuotaExceededException>(() => client.GetPlaylistAsync("PL1", CancellationToken.None));
    }

    [Fact]
    public async Task Playlist_not_found_is_reported()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(
            """{"error":{"code":404,"message":"x","errors":[{"reason":"playlistNotFound"}]}}""", HttpStatusCode.NotFound));
        var client = new YouTubeApiClient(new FakeHttpClientFactory(handler, "https://www.googleapis.com/youtube/v3/"), NullLogger<YouTubeApiClient>.Instance);

        var ex = await Assert.ThrowsAsync<ApiException>(() => client.GetPlaylistAsync("PL1", CancellationToken.None));
        Assert.Equal("playlistNotFound", ex.Reason);
        Assert.False(ex.IsTransient);
    }

    [Fact]
    public async Task Lists_user_playlists_with_pagination()
    {
        var handler = new FakeHttpHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("playlists?") && !url.Contains("pageToken"))
                return FakeHttpHandler.Json("""{"nextPageToken":"N","items":[{"id":"PL1","snippet":{"title":"One","channelTitle":"Me"},"contentDetails":{"itemCount":3}}]}""");
            if (url.Contains("playlists?") && url.Contains("pageToken=N"))
                return FakeHttpHandler.Json("""{"items":[{"id":"PL2","snippet":{"title":"Two"},"contentDetails":{"itemCount":7}}]}""");
            if (url.Contains("channels"))
                return FakeHttpHandler.Json("""{"items":[{"contentDetails":{"relatedPlaylists":{"likes":""}}}]}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var client = new YouTubeApiClient(new FakeHttpClientFactory(handler, "https://www.googleapis.com/youtube/v3/"), NullLogger<YouTubeApiClient>.Instance);

        var lists = await client.GetMyPlaylistsAsync(CancellationToken.None);
        Assert.Equal(["One", "Two"], lists.Select(l => l.Title));
        Assert.Equal(7, lists[1].ItemCount);
    }
}

public class SpotifyApiClientTests
{
    private const string TrackJson = """
        {"id":"abc","uri":"spotify:track:abc","name":"Midnight Static","duration_ms":214000,"explicit":false,"track_number":3,"disc_number":1,
         "preview_url":null,"external_ids":{"isrc":"FRXYZ2400001"},"external_urls":{"spotify":"https://open.spotify.com/track/abc"},
         "artists":[{"name":"Neon Harbor"},{"name":"Guest"}],
         "album":{"id":"alb","name":"Coastal Frequencies","release_date":"2024-03-15","artists":[{"name":"Neon Harbor"}],
                  "images":[{"url":"https://i.scdn.co/640","width":640},{"url":"https://i.scdn.co/300","width":300}]}}
        """;

    private static SpotifyApiClient Create(FakeHttpHandler handler) =>
        new(new FakeHttpClientFactory(handler, "https://api.spotify.com/v1/"), new TestSettingsService());

    [Fact]
    public async Task Search_maps_all_metadata()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("{\"tracks\":{\"items\":[" + TrackJson + "]}}"));
        var results = await Create(handler).SearchTracksAsync("track:\"midnight static\"", 10, 0, CancellationToken.None);

        var c = Assert.Single(results);
        Assert.Equal("Midnight Static", c.Title);
        Assert.Equal(["Neon Harbor", "Guest"], c.Artists);
        Assert.Equal("Coastal Frequencies", c.Album);
        Assert.Equal("2024-03-15", c.ReleaseDate);
        Assert.Equal("FRXYZ2400001", c.Isrc);
        Assert.Equal(214_000, c.DurationMs);
        Assert.Equal("https://i.scdn.co/640", c.CoverUrl);
        Assert.Contains("type=track", handler.Requests[0].Url);
        Assert.Contains("limit=10", handler.Requests[0].Url);
        Assert.Contains("market=from_token", handler.Requests[0].Url);
    }

    [Fact]
    public async Task Creates_playlist_with_me_endpoint_and_adds_items_endpoint()
    {
        var handler = new FakeHttpHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/v1/me/playlists" => FakeHttpHandler.Json("""{"id":"pl1","name":"Out","owner":{"id":"me"},"public":false,"collaborative":false,"items":{"total":0},"external_urls":{"spotify":"https://open.spotify.com/playlist/pl1"}}""", HttpStatusCode.Created),
            "/v1/playlists/pl1/items" => FakeHttpHandler.Json("""{"snapshot_id":"s"}""", HttpStatusCode.Created),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        var client = Create(handler);

        var created = await client.CreatePlaylistAsync("Out", null, false, CancellationToken.None);
        await client.AddItemsAsync(created.Id, ["spotify:track:a", "spotify:track:b"], CancellationToken.None);

        Assert.Equal("pl1", created.Id);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Contains("\"public\":false", handler.Requests[0].Body);
        Assert.EndsWith("/v1/playlists/pl1/items", handler.Requests[1].Url);
        using var body = JsonDocument.Parse(handler.Requests[1].Body!);
        Assert.Equal(["spotify:track:a", "spotify:track:b"], body.RootElement.GetProperty("uris").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Add_items_rejects_more_than_100()
    {
        var client = Create(new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.AddItemsAsync("pl", Enumerable.Range(0, 101).Select(i => $"spotify:track:{i}").ToList(), CancellationToken.None));
    }

    [Fact]
    public async Task Playlist_items_supports_new_and_legacy_formats_with_paging()
    {
        var handler = new FakeHttpHandler(req => req.RequestUri!.Query.Contains("offset=50")
            ? FakeHttpHandler.Json("""{"items":[{"track":{"uri":"spotify:track:legacy"}}],"next":null}""")
            : FakeHttpHandler.Json("""{"items":[{"item":{"uri":"spotify:track:new"}},{"item":null}],"next":"https://api.spotify.com/v1/playlists/p/items?offset=50&limit=50"}"""));

        var uris = await Create(handler).GetPlaylistItemUrisAsync("p", CancellationToken.None);
        Assert.Equal(["spotify:track:new", "spotify:track:legacy"], uris);
    }

    [Fact]
    public async Task Errors_are_translated()
    {
        var tooMany = new FakeHttpHandler(_ =>
        {
            var r = FakeHttpHandler.Json("""{"error":{"status":429,"message":"API rate limit exceeded"}}""", HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
            return r;
        });
        var quota = await Assert.ThrowsAsync<QuotaExceededException>(() => Create(tooMany).SearchTracksAsync("x", 5, 0, CancellationToken.None));
        Assert.Equal(TimeSpan.FromMinutes(10), quota.RetryAfter);

        var forbidden = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{"error":{"status":403,"message":"Forbidden"}}""", HttpStatusCode.Forbidden));
        var ex = await Assert.ThrowsAsync<ApiException>(() => Create(forbidden).GetCurrentUserAsync(CancellationToken.None));
        Assert.Contains("mode développement", ex.Message);
    }
}

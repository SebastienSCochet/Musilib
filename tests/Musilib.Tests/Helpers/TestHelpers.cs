using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Musilib.Application.Abstractions;
using Musilib.Application.Settings;
using Musilib.Domain.Entities;
using Musilib.Domain.Enums;
using Musilib.Infrastructure.Persistence;

namespace Musilib.Tests.Helpers;

/// <summary>Gestionnaire HTTP simulé : enregistre les requêtes et renvoie des réponses programmées.</summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, int, Task<HttpResponseMessage>> _responder;

    public FakeHttpHandler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> responder) => _responder = responder;

    public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : this((r, _) => Task.FromResult(responder(r))) { }

    public List<(HttpMethod Method, string Url, string? Body)> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        int index;
        lock (Requests)
        {
            Requests.Add((request.Method, request.RequestUri!.ToString(), body));
            index = Requests.Count - 1;
        }
        return await _responder(request, index);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public sealed class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;
    private readonly Uri? _baseAddress;

    public FakeHttpClientFactory(HttpMessageHandler handler, string? baseAddress = null)
    {
        _handler = handler;
        _baseAddress = baseAddress is null ? null : new Uri(baseAddress);
    }

    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false) { BaseAddress = _baseAddress };
}

public sealed class TestSettingsService : ISettingsService
{
    public AppSettings Current { get; private set; } = new();
    public string SettingsPath => "memory";
    public event EventHandler<AppSettings>? Changed;

    public Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        Current = settings;
        Changed?.Invoke(this, settings);
        return Task.CompletedTask;
    }
}

/// <summary>Base SQLite temporaire et ses dépôts (tests d'intégration de persistance).</summary>
public sealed class TempDatabase : IAsyncDisposable
{
    private readonly string _directory;

    private TempDatabase(string directory, SqliteConnectionFactory factory)
    {
        _directory = directory;
        Factory = factory;
        Tracks = new TrackRepository(factory);
        Playlists = new PlaylistRepository(factory, Tracks);
        Jobs = new JobRepository(factory);
        History = new HistoryRepository(factory);
        Cache = new SearchCacheRepository(factory, TimeProvider.System);
    }

    public SqliteConnectionFactory Factory { get; }
    public TrackRepository Tracks { get; }
    public PlaylistRepository Playlists { get; }
    public JobRepository Jobs { get; }
    public HistoryRepository History { get; }
    public SearchCacheRepository Cache { get; }
    public string Path => Factory.DatabasePath;

    public static async Task<TempDatabase> CreateAsync()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "musilib-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var factory = new SqliteConnectionFactory(System.IO.Path.Combine(dir, "test.db"));
        await new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
        return new TempDatabase(dir, factory);
    }

    public async Task<(Playlist Playlist, List<Track> Tracks)> SeedPlaylistAsync(int trackCount, Action<Track, int>? configure = null)
    {
        var playlist = new Playlist { Source = PlaylistSource.YouTube, ExternalId = "PL" + Guid.NewGuid().ToString("N"), Title = "Test" };
        await Playlists.UpsertAsync(playlist);
        var tracks = new List<Track>();
        for (var i = 0; i < trackCount; i++)
        {
            var t = new Track
            {
                PlaylistId = playlist.Id,
                Position = i,
                SourceItemId = "item" + i,
                SourceVideoId = "vid" + i,
                SourceTitle = $"Artist {i} - Song {i}",
                SourceDurationMs = 200_000 + i * 1000,
                Metadata = new TrackMetadata { Title = $"Song {i}", Artists = [$"Artist {i}"], DurationMs = 200_000 + i * 1000 }
            };
            configure?.Invoke(t, i);
            tracks.Add(t);
        }
        await Tracks.UpsertManyAsync(tracks);
        return (playlist, tracks);
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return ValueTask.CompletedTask;
    }
}

public static class Candidates
{
    public static SpotifyCandidate Make(string id, string title, string artist, long durationMs, string? isrc = null, string? album = "Album") => new()
    {
        SpotifyId = id,
        Uri = "spotify:track:" + id,
        Title = title,
        Artists = [artist],
        AlbumArtists = [artist],
        Album = album,
        DurationMs = durationMs,
        Isrc = isrc,
        ReleaseDate = "2024-01-01"
    };
}

/// <summary>Génère un fichier WAV PCM minimal (lisible et taggable par TagLib).</summary>
public static class WavFactory
{
    public static string Create(string path, double seconds = 0.5)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        const int rate = 8000;
        var samples = (int)(rate * seconds);
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8.ToArray()); w.Write(36 + samples * 2); w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8.ToArray()); w.Write(samples * 2);
        for (var i = 0; i < samples; i++) w.Write((short)(Math.Sin(i / 10.0) * 8000));
        return path;
    }
}

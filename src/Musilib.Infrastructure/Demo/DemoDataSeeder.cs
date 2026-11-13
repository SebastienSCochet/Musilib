using Microsoft.Extensions.Logging;
using Musilib.Application.Abstractions;
using Musilib.Application.Services;
using Musilib.Domain.Entities;
using Musilib.Domain.Enums;

namespace Musilib.Infrastructure.Demo;

/// <summary>Prépare une base de démonstration réaliste au premier lancement en mode démo.</summary>
public sealed class DemoDataSeeder
{
    private readonly IPlaylistRepository _playlists;
    private readonly ITrackRepository _tracks;
    private readonly IHistoryRepository _history;
    private readonly PlaylistImportService _import;
    private readonly MatchingService _matching;
    private readonly IAudioMetadataService _audio;
    private readonly IAppPaths _paths;
    private readonly ILogger<DemoDataSeeder> _logger;

    public DemoDataSeeder(IPlaylistRepository playlists, ITrackRepository tracks, IHistoryRepository history, PlaylistImportService import,
        MatchingService matching, IAudioMetadataService audio, IAppPaths paths, ILogger<DemoDataSeeder> logger)
    {
        _playlists = playlists;
        _tracks = tracks;
        _history = history;
        _import = import;
        _matching = matching;
        _audio = audio;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>Dossier contenant des fichiers audio fictifs « possédés » par l'utilisateur de démonstration.</summary>
    public string DemoMusicFolder => Path.Combine(_paths.DataDirectory, "demo-music");

    public async Task SeedAsync(CancellationToken ct)
    {
        await EnsureDemoMusicAsync(ct).ConfigureAwait(false);
        if ((await _playlists.GetAllAsync(ct).ConfigureAwait(false)).Count > 0) return;

        _logger.LogInformation("Initialisation des données de démonstration");
        await _history.AddAsync(new HistoryEntry { Kind = HistoryKind.Connection, Title = "Connexion YouTube (démo)", Details = "Chaîne de démonstration" }, ct).ConfigureAwait(false);
        await _history.AddAsync(new HistoryEntry { Kind = HistoryKind.Connection, Title = "Connexion Spotify (démo)", Details = "Utilisateur démo" }, ct).ConfigureAwait(false);

        var first = await _import.ImportAsync(DemoCatalog.Playlists[0].Id, null, ct).ConfigureAwait(false);
        await _import.ImportAsync(DemoCatalog.Playlists[1].Id, null, ct).ConfigureAwait(false);

        var tracks = await _tracks.GetByPlaylistAsync(first.Playlist.Id, ct).ConfigureAwait(false);
        foreach (var t in tracks) await _matching.MatchTrackAsync(t, false, ct).ConfigureAwait(false);
        await _history.AddAsync(new HistoryEntry
        {
            Kind = HistoryKind.Match,
            PlaylistId = first.Playlist.Id,
            Title = $"Recherche Spotify · {first.Playlist.Title}",
            Details = "Analyse initiale de démonstration"
        }, ct).ConfigureAwait(false);
    }

    private async Task EnsureDemoMusicAsync(CancellationToken ct)
    {
        if (Directory.Exists(DemoMusicFolder) && Directory.EnumerateFiles(DemoMusicFolder).Any()) return;
        Directory.CreateDirectory(DemoMusicFolder);
        var keys = new[] { "nh-static", "lr-velours", "vs-orbit", "km-sakura", "cv-mirror", "oa-ember", "nb-aube", "pc-kites", "km-rain", "cv-quiet" };
        var i = 0;
        foreach (var key in keys)
        {
            var song = DemoCatalog.FindSong(key)!;
            // Noms variés : certains fichiers sont taggués, d'autres seulement nommés.
            var name = i % 3 == 0 ? $"{i + 1:00} {song.Title}.wav" : $"{song.Artist} - {song.Title}.wav";
            var path = Path.Combine(DemoMusicFolder, Domain.Services.FileNameSanitizer.Sanitize(Path.GetFileNameWithoutExtension(name)) + ".wav");
            DemoAudio.WriteWav(path, 6, i);
            if (i % 3 == 0)
            {
                await _audio.WriteAsync(path, new TrackMetadata { Title = song.Title, Artists = [song.Artist], Album = song.Album }, null, null, ct).ConfigureAwait(false);
            }
            i++;
        }
    }
}

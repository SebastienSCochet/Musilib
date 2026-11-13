using Microsoft.Extensions.Logging;
using Musilib.Application.Abstractions;
using Musilib.Domain.Entities;
using Musilib.Domain.Services;

namespace Musilib.Application.Services;

public sealed record LocalScanResult(int FilesScanned, int Associated, int AlreadyAssociated, IReadOnlyList<string> Unmatched);

/// <summary>
/// Associe automatiquement des fichiers audio possédés par l'utilisateur aux morceaux d'une playlist
/// (comparaison des tags ou du nom de fichier avec les métadonnées).
/// </summary>
public sealed class LocalLibraryService
{
    public const double AssociationThreshold = 0.80;

    private readonly IAudioMetadataService _audio;
    private readonly ITrackRepository _tracks;
    private readonly ILogger<LocalLibraryService> _logger;

    public LocalLibraryService(IAudioMetadataService audio, ITrackRepository tracks, ILogger<LocalLibraryService> logger)
    {
        _audio = audio;
        _tracks = tracks;
        _logger = logger;
    }

    public async Task<LocalScanResult> ScanAndAssociateAsync(string folder, Guid playlistId, bool replaceExisting,
        IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);

        var files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
            .Where(_audio.IsSupported)
            .ToList();
        var tracks = (await _tracks.GetByPlaylistAsync(playlistId, ct).ConfigureAwait(false)).ToList();
        var targets = tracks.Where(t => replaceExisting || !t.HasLocalFile).ToList();
        var alreadyAssociated = tracks.Count - targets.Count;

        var fileQueries = new List<(string Path, MatchQuery Query)>();
        var done = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            fileQueries.Add((file, await BuildFileQueryAsync(file, ct).ConfigureAwait(false)));
            progress?.Report((++done, files.Count));
        }

        // Scores de toutes les paires plausibles, puis affectation gloutonne (chaque fichier et morceau une seule fois).
        var pairs = new List<(double Score, Track Track, string File)>();
        foreach (var (path, query) in fileQueries)
        {
            var queryTokens = TextNormalizer.Tokens(query.Title).ToHashSet();
            foreach (var track in targets)
            {
                var trackTokens = TextNormalizer.Tokens(track.Metadata.Title);
                if (queryTokens.Count > 0 && !trackTokens.Any(queryTokens.Contains)) continue;
                var candidate = new SpotifyCandidate
                {
                    SpotifyId = track.Id.ToString(),
                    Title = track.Metadata.Title,
                    Artists = track.Metadata.Artists,
                    DurationMs = track.Metadata.DurationMs ?? track.SourceDurationMs ?? 0,
                    Isrc = track.Metadata.Isrc
                };
                MatchScorer.Score(query, candidate);
                if (candidate.Score >= AssociationThreshold) pairs.Add((candidate.Score, track, path));
            }
        }

        var usedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedTracks = new HashSet<Guid>();
        var updated = new List<Track>();
        foreach (var (_, track, file) in pairs.OrderByDescending(p => p.Score))
        {
            if (usedFiles.Contains(file) || usedTracks.Contains(track.Id)) continue;
            usedFiles.Add(file);
            usedTracks.Add(track.Id);
            track.LocalFilePath = file;
            track.Touch();
            updated.Add(track);
        }
        if (updated.Count > 0) await _tracks.UpsertManyAsync(updated, ct).ConfigureAwait(false);

        _logger.LogInformation("Scan local : {Files} fichiers, {Associated} associations", files.Count, updated.Count);
        var unmatched = files.Where(f => !usedFiles.Contains(f)).Select(Path.GetFileName).OfType<string>().ToList();
        return new LocalScanResult(files.Count, updated.Count, alreadyAssociated, unmatched);
    }

    private async Task<MatchQuery> BuildFileQueryAsync(string file, CancellationToken ct)
    {
        AudioFileInfo? info = null;
        try { info = await _audio.ReadAsync(file, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogDebug(ex, "Tags illisibles"); }

        if (info is not null && !string.IsNullOrWhiteSpace(info.Metadata.Title) && info.Metadata.Artists.Count > 0)
            return new MatchQuery(info.Metadata.Title, info.Metadata.Artists, info.DurationMs, info.Metadata.Isrc);

        var parsed = YouTubeTitleParser.Parse(Path.GetFileNameWithoutExtension(file), null);
        return new MatchQuery(parsed.Title, parsed.Artists, info?.DurationMs, null, Path.GetFileNameWithoutExtension(file));
    }
}

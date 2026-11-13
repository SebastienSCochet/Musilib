using Microsoft.Extensions.Logging;
using Musilib.Application.Abstractions;
using Musilib.Domain.Entities;
using Musilib.Domain.Enums;
using Musilib.Domain.Services;

namespace Musilib.Application.Services;

public sealed record ImportProgress(int Processed, int Total, string? CurrentTitle);

public sealed record ImportResult(Playlist Playlist, int Added, int Updated, int Removed, int Unavailable);

/// <summary>
/// Importe (ou resynchronise) une playlist YouTube. L'import est idempotent : les morceaux existants
/// conservent leurs correspondances et modifications, seules la position et la disponibilité sont mises à jour.
/// Les données sont sauvegardées par lots pour ne jamais perdre l'état en cas d'interruption.
/// </summary>
public sealed class PlaylistImportService
{
    private const int BatchSize = 50;

    private readonly IYouTubeClient _youTube;
    private readonly IPlaylistRepository _playlists;
    private readonly ITrackRepository _tracks;
    private readonly IHistoryRepository _history;
    private readonly ILogger<PlaylistImportService> _logger;
    private readonly PlaylistSource _source;

    public PlaylistImportService(
        IYouTubeClient youTube,
        IPlaylistRepository playlists,
        ITrackRepository tracks,
        IHistoryRepository history,
        ILogger<PlaylistImportService> logger,
        PlaylistSourceContext sourceContext)
    {
        _youTube = youTube;
        _playlists = playlists;
        _tracks = tracks;
        _history = history;
        _logger = logger;
        _source = sourceContext.Source;
    }

    public Task<IReadOnlyList<RemotePlaylist>> GetRemotePlaylistsAsync(CancellationToken ct) => _youTube.GetMyPlaylistsAsync(ct);

    public async Task<ImportResult> ImportAsync(string playlistId, IProgress<ImportProgress>? progress, CancellationToken ct)
    {
        var remote = await _youTube.GetPlaylistAsync(playlistId, ct).ConfigureAwait(false)
                     ?? throw new InvalidOperationException($"Playlist introuvable ou inaccessible : {playlistId}");

        var playlist = await _playlists.FindByExternalIdAsync(_source, remote.Id, ct).ConfigureAwait(false) ?? new Playlist
        {
            Source = _source,
            ExternalId = remote.Id,
            ImportedAt = DateTimeOffset.UtcNow
        };
        playlist.Title = remote.Title;
        playlist.Description = remote.Description;
        playlist.ThumbnailUrl = remote.ThumbnailUrl;
        playlist.Owner = remote.Owner;
        playlist.SourceItemCount = remote.ItemCount;
        await _playlists.UpsertAsync(playlist, ct).ConfigureAwait(false);

        var existing = (await _tracks.GetByPlaylistAsync(playlist.Id, ct).ConfigureAwait(false))
            .ToDictionary(t => t.SourceItemId, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var batch = new List<Track>(BatchSize);
        int added = 0, updated = 0, unavailable = 0, processed = 0, position = 0;

        await foreach (var item in _youTube.GetPlaylistItemsAsync(remote.Id, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (!seen.Add(item.ItemId)) continue;

            Track track;
            if (existing.TryGetValue(item.ItemId, out var found))
            {
                track = found;
                updated++;
            }
            else
            {
                track = new Track { PlaylistId = playlist.Id, SourceItemId = item.ItemId };
                added++;
            }

            // Position séquentielle : conserve l'ordre original même si l'API renvoie des positions incohérentes.
            track.Position = position++;
            track.SourceVideoId = item.VideoId;
            track.SourceChannel = item.ChannelTitle;
            track.SourceThumbnailUrl = item.ThumbnailUrl ?? track.SourceThumbnailUrl;
            track.SourceDurationMs = item.DurationMs ?? track.SourceDurationMs;
            track.Availability = item.Availability;
            track.RemovedFromSource = false;
            if (item.Availability == SourceAvailability.Available || string.IsNullOrEmpty(track.SourceTitle))
                track.SourceTitle = item.Title;

            if (item.Availability != SourceAvailability.Available)
            {
                unavailable++;
                if (track.MatchStatus == MatchStatus.Pending) track.LastError = DescribeAvailability(item.Availability);
            }

            if (!track.MetadataEdited && track.MatchStatus is MatchStatus.Pending or MatchStatus.NoMatch or MatchStatus.Error)
            {
                var parsed = YouTubeTitleParser.Parse(track.SourceTitle, track.SourceChannel);
                track.Metadata.Title = parsed.Title;
                track.Metadata.Artists = parsed.Artists.ToList();
                track.Metadata.DurationMs = track.SourceDurationMs;
                track.Metadata.CoverUrl ??= track.SourceThumbnailUrl;
                track.MetadataOrigin = MetadataOrigin.Source;
            }
            track.Touch();
            batch.Add(track);
            processed++;

            if (batch.Count >= BatchSize)
            {
                await _tracks.UpsertManyAsync(batch, ct).ConfigureAwait(false);
                batch.Clear();
            }
            progress?.Report(new ImportProgress(processed, Math.Max(remote.ItemCount, processed), item.Title));
        }

        if (batch.Count > 0) await _tracks.UpsertManyAsync(batch, ct).ConfigureAwait(false);

        // Morceaux retirés de la playlist source : conservés (avec leurs données) mais marqués.
        var removed = existing.Values.Where(t => !seen.Contains(t.SourceItemId) && !t.RemovedFromSource).ToList();
        foreach (var t in removed)
        {
            t.RemovedFromSource = true;
            t.Position = position++;
            t.Touch();
        }
        if (removed.Count > 0) await _tracks.UpsertManyAsync(removed, ct).ConfigureAwait(false);

        playlist.LastSyncedAt = DateTimeOffset.UtcNow;
        playlist.SourceItemCount = processed;
        await _playlists.UpsertAsync(playlist, ct).ConfigureAwait(false);

        await _history.AddAsync(new HistoryEntry
        {
            Kind = HistoryKind.Import,
            PlaylistId = playlist.Id,
            Title = $"Import de « {playlist.Title} »",
            Details = $"{processed} morceaux ({added} nouveaux, {updated} mis à jour, {removed.Count} retirés, {unavailable} indisponibles)",
            Success = true
        }, ct).ConfigureAwait(false);

        _logger.LogInformation("Playlist {PlaylistId} importée : {Count} éléments, {Unavailable} indisponibles", playlist.Id, processed, unavailable);
        return new ImportResult(playlist, added, updated, removed.Count, unavailable);
    }

    public static string DescribeAvailability(SourceAvailability availability) => availability switch
    {
        SourceAvailability.Deleted => "Vidéo supprimée de YouTube",
        SourceAvailability.Private => "Vidéo privée",
        SourceAvailability.Unavailable => "Vidéo indisponible",
        _ => string.Empty
    };
}

/// <summary>Indique la source des playlists (YouTube réel ou démonstration).</summary>
public sealed record PlaylistSourceContext(PlaylistSource Source);

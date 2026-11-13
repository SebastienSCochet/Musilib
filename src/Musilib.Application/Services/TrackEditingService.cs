using Musilib.Application.Abstractions;
using Musilib.Domain.Entities;
using Musilib.Domain.Enums;

namespace Musilib.Application.Services;

/// <summary>Édition et sauvegarde des métadonnées, pochettes, fichiers locaux et exclusions.</summary>
public sealed class TrackEditingService
{
    private readonly ITrackRepository _tracks;
    private readonly IHistoryRepository _history;
    private readonly IAudioMetadataService _audio;

    public TrackEditingService(ITrackRepository tracks, IHistoryRepository history, IAudioMetadataService audio)
    {
        _tracks = tracks;
        _history = history;
        _audio = audio;
    }

    public async Task SaveMetadataAsync(Track track, TrackMetadata metadata, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (string.IsNullOrWhiteSpace(metadata.Title))
            throw new ArgumentException("Le titre est obligatoire.", nameof(metadata));

        metadata.Artists = metadata.Artists.Select(a => a.Trim()).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        metadata.AlbumArtists = metadata.AlbumArtists.Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
        if (!string.IsNullOrWhiteSpace(metadata.ReleaseDate) && !IsValidReleaseDate(metadata.ReleaseDate))
            throw new ArgumentException("La date de sortie doit être au format AAAA, AAAA-MM ou AAAA-MM-JJ.", nameof(metadata));

        track.Metadata = metadata;
        track.MetadataEdited = true;
        track.MetadataOrigin = MetadataOrigin.Manual;
        track.Touch();
        await _tracks.UpdateAsync(track, ct).ConfigureAwait(false);
        await _history.AddAsync(new HistoryEntry
        {
            Kind = HistoryKind.Edit,
            PlaylistId = track.PlaylistId,
            Title = $"Métadonnées modifiées : {metadata.ArtistsDisplay} - {metadata.Title}"
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Annule les modifications manuelles et réapplique les données de la correspondance Spotify.</summary>
    public async Task RevertToSpotifyAsync(Track track, SpotifyCandidate candidate, CancellationToken ct)
    {
        track.MetadataEdited = false;
        track.ApplyCandidate(candidate, track.MatchStatus is MatchStatus.Confirmed ? MatchStatus.Confirmed : MatchStatus.Confirmed, overwriteMetadata: true);
        await _tracks.UpdateAsync(track, ct).ConfigureAwait(false);
    }

    public async Task SetExcludedAsync(Track track, bool excluded, CancellationToken ct)
    {
        track.Excluded = excluded;
        track.Touch();
        await _tracks.UpdateAsync(track, ct).ConfigureAwait(false);
    }

    public async Task SetCustomSearchQueryAsync(Track track, string? query, CancellationToken ct)
    {
        track.CustomSearchQuery = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        track.Touch();
        await _tracks.UpdateAsync(track, ct).ConfigureAwait(false);
    }

    /// <summary>Associe un fichier audio local possédé par l'utilisateur au morceau.</summary>
    public async Task AssociateLocalFileAsync(Track track, string? path, CancellationToken ct)
    {
        if (path is not null)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Fichier introuvable.", path);
            if (!_audio.IsSupported(path)) throw new NotSupportedException($"Format audio non pris en charge : {Path.GetExtension(path)}");
        }
        track.LocalFilePath = path;
        track.Touch();
        await _tracks.UpdateAsync(track, ct).ConfigureAwait(false);
    }

    public static bool IsValidReleaseDate(string value)
    {
        string[] formats = ["yyyy", "yyyy-MM", "yyyy-MM-dd"];
        return DateTime.TryParseExact(value.Trim(), formats, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out _);
    }
}

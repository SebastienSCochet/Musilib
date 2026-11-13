using Musilib.Domain.Enums;

namespace Musilib.Domain.Entities;

public sealed class Track
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PlaylistId { get; set; }
    /// <summary>Position d'origine dans la playlist source (0-based). Garantit la conservation de l'ordre.</summary>
    public int Position { get; set; }

    // --- Source (YouTube) ---
    public string SourceItemId { get; set; } = string.Empty;
    public string SourceVideoId { get; set; } = string.Empty;
    public string SourceTitle { get; set; } = string.Empty;
    public string? SourceChannel { get; set; }
    public long? SourceDurationMs { get; set; }
    public string? SourceThumbnailUrl { get; set; }
    public SourceAvailability Availability { get; set; } = SourceAvailability.Available;
    public bool RemovedFromSource { get; set; }

    // --- Métadonnées ---
    public TrackMetadata Metadata { get; set; } = new();
    public MetadataOrigin MetadataOrigin { get; set; } = MetadataOrigin.Source;
    /// <summary>Vrai si l'utilisateur a modifié manuellement les métadonnées : elles ne seront plus écrasées automatiquement.</summary>
    public bool MetadataEdited { get; set; }

    // --- Correspondance Spotify ---
    public MatchStatus MatchStatus { get; set; } = MatchStatus.Pending;
    public double? MatchScore { get; set; }
    public string? SpotifyTrackId { get; set; }
    public string? SpotifyUri { get; set; }
    public string? SpotifyUrl { get; set; }
    public string? PreviewUrl { get; set; }
    /// <summary>Requête de recherche personnalisée saisie par l'utilisateur.</summary>
    public string? CustomSearchQuery { get; set; }

    // --- Fichier local ---
    public string? LocalFilePath { get; set; }

    // --- Divers ---
    /// <summary>Exclu des transferts/exports par l'utilisateur.</summary>
    public bool Excluded { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsAvailable => Availability == SourceAvailability.Available && !RemovedFromSource;
    public bool HasSpotifyMatch => !string.IsNullOrEmpty(SpotifyUri) && MatchStatus is MatchStatus.AutoMatched or MatchStatus.Confirmed;
    public bool HasLocalFile => !string.IsNullOrWhiteSpace(LocalFilePath);

    /// <summary>Applique la correspondance choisie. Les métadonnées éditées manuellement ne sont jamais écrasées.</summary>
    public void ApplyCandidate(SpotifyCandidate candidate, MatchStatus status, bool overwriteMetadata)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        SpotifyTrackId = candidate.SpotifyId;
        SpotifyUri = candidate.Uri;
        SpotifyUrl = candidate.ExternalUrl;
        PreviewUrl = candidate.PreviewUrl;
        MatchScore = candidate.Score;
        MatchStatus = status;
        LastError = null;

        if (overwriteMetadata && !MetadataEdited)
        {
            Metadata.Title = candidate.Title;
            Metadata.Artists = new List<string>(candidate.Artists);
            Metadata.Album = candidate.Album;
            Metadata.AlbumArtists = new List<string>(candidate.AlbumArtists);
            Metadata.ReleaseDate = candidate.ReleaseDate;
            Metadata.DurationMs = candidate.DurationMs;
            Metadata.Isrc = candidate.Isrc;
            Metadata.CoverUrl = candidate.CoverUrl ?? Metadata.CoverUrl;
            Metadata.TrackNumber = candidate.TrackNumber;
            Metadata.DiscNumber = candidate.DiscNumber;
            MetadataOrigin = MetadataOrigin.Spotify;
        }
        Touch();
    }

    public void ClearMatch(MatchStatus status)
    {
        SpotifyTrackId = null;
        SpotifyUri = null;
        SpotifyUrl = null;
        PreviewUrl = null;
        MatchScore = null;
        MatchStatus = status;
        Touch();
    }

    public void Touch() => UpdatedAt = DateTimeOffset.UtcNow;
}

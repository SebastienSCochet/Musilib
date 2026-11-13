namespace Musilib.Domain.Entities;

/// <summary>Résultat de recherche Spotify proposé comme correspondance possible.</summary>
public sealed class SpotifyCandidate
{
    public string SpotifyId { get; set; } = string.Empty;
    public string Uri { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public List<string> Artists { get; set; } = new();
    public List<string> AlbumArtists { get; set; } = new();
    public string? Album { get; set; }
    public string? AlbumId { get; set; }
    public string? ReleaseDate { get; set; }
    public long DurationMs { get; set; }
    public string? Isrc { get; set; }
    public string? CoverUrl { get; set; }
    public string? PreviewUrl { get; set; }
    public string? ExternalUrl { get; set; }
    public bool Explicit { get; set; }
    public int? TrackNumber { get; set; }
    public int? DiscNumber { get; set; }

    // Score de correspondance calculé localement (0..1)
    public double Score { get; set; }
    public double TitleScore { get; set; }
    public double ArtistScore { get; set; }
    public double DurationScore { get; set; }

    public string ArtistsDisplay => string.Join(", ", Artists);
}

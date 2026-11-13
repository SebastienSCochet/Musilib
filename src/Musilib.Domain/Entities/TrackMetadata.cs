namespace Musilib.Domain.Entities;

/// <summary>Métadonnées éditables d'un morceau.</summary>
public sealed class TrackMetadata
{
    public string Title { get; set; } = string.Empty;
    public List<string> Artists { get; set; } = new();
    public string? Album { get; set; }
    public List<string> AlbumArtists { get; set; } = new();
    /// <summary>Date de sortie au format Spotify (YYYY, YYYY-MM ou YYYY-MM-DD).</summary>
    public string? ReleaseDate { get; set; }
    public long? DurationMs { get; set; }
    public string? Isrc { get; set; }
    public string? CoverUrl { get; set; }
    /// <summary>Pochette personnalisée choisie par l'utilisateur (fichier local).</summary>
    public string? CoverLocalPath { get; set; }
    public string? Genre { get; set; }
    public int? TrackNumber { get; set; }
    public int? DiscNumber { get; set; }
    public string? Comment { get; set; }

    public string ArtistsDisplay => Artists.Count == 0 ? string.Empty : string.Join(", ", Artists);
    public string PrimaryArtist => Artists.Count == 0 ? string.Empty : Artists[0];

    public int? Year
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ReleaseDate) || ReleaseDate.Length < 4) return null;
            return int.TryParse(ReleaseDate.AsSpan(0, 4), out var y) ? y : null;
        }
    }

    public TrackMetadata Clone() => new()
    {
        Title = Title,
        Artists = new List<string>(Artists),
        Album = Album,
        AlbumArtists = new List<string>(AlbumArtists),
        ReleaseDate = ReleaseDate,
        DurationMs = DurationMs,
        Isrc = Isrc,
        CoverUrl = CoverUrl,
        CoverLocalPath = CoverLocalPath,
        Genre = Genre,
        TrackNumber = TrackNumber,
        DiscNumber = DiscNumber,
        Comment = Comment
    };
}

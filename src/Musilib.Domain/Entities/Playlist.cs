using Musilib.Domain.Enums;

namespace Musilib.Domain.Entities;

public sealed class Playlist
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public PlaylistSource Source { get; set; }
    /// <summary>Identifiant de la playlist chez le fournisseur (ex. ID YouTube).</summary>
    public string ExternalId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? ThumbnailUrl { get; set; }
    public string? Owner { get; set; }
    public int SourceItemCount { get; set; }
    public DateTimeOffset? ImportedAt { get; set; }
    public DateTimeOffset? LastSyncedAt { get; set; }

    /// <summary>Statistiques calculées (non persistées directement).</summary>
    public PlaylistStats Stats { get; set; } = new();
}

public sealed class PlaylistStats
{
    public int Total { get; set; }
    public int Unavailable { get; set; }
    public int Pending { get; set; }
    public int AutoMatched { get; set; }
    public int Ambiguous { get; set; }
    public int NoMatch { get; set; }
    public int Confirmed { get; set; }
    public int Errors { get; set; }
    public int Excluded { get; set; }
    public int WithLocalFile { get; set; }

    public int Matched => AutoMatched + Confirmed;
    public int NeedsReview => Ambiguous + NoMatch + Errors;
    public double MatchedRatio => Total == 0 ? 0 : (double)Matched / Total;
}

using Musilib.Domain.Entities;
using Musilib.Domain.Enums;

namespace Musilib.Domain.Services;

/// <summary>Seuils de décision pour la validation automatique des correspondances.</summary>
public sealed record MatchThresholds(double AutoAccept = 0.85, double Minimum = 0.50, double AmbiguityGap = 0.07)
{
    public static MatchThresholds Default { get; } = new();
}

/// <summary>Référence à comparer (issue de la source YouTube ou des métadonnées éditées).</summary>
public sealed record MatchQuery(string Title, IReadOnlyList<string> Artists, long? DurationMs, string? Isrc = null, string? RawSourceTitle = null);

public sealed record MatchDecision(MatchStatus Status, SpotifyCandidate? Best, IReadOnlyList<SpotifyCandidate> Ranked, string Reason);

/// <summary>
/// Calcule un score de correspondance (titre 50 %, artistes 30 %, durée 20 %) et décide si la
/// correspondance est fiable, ambiguë ou absente. Une correspondance incertaine n'est jamais validée
/// automatiquement.
/// </summary>
public static class MatchScorer
{
    public const double TitleWeight = 0.5;
    public const double ArtistWeight = 0.3;
    public const double DurationWeight = 0.2;

    public static void Score(MatchQuery query, SpotifyCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidate);

        candidate.TitleScore = TitleScore(query.Title, candidate.Title);
        candidate.ArtistScore = ArtistScore(query, candidate.Artists);
        candidate.DurationScore = DurationScore(query.DurationMs, candidate.DurationMs);

        double total;
        if (query.DurationMs is null or <= 0)
        {
            total = candidate.TitleScore * 0.6 + candidate.ArtistScore * 0.4;
        }
        else
        {
            total = candidate.TitleScore * TitleWeight + candidate.ArtistScore * ArtistWeight + candidate.DurationScore * DurationWeight;
        }

        // Un ISRC identique est une preuve forte.
        if (!string.IsNullOrWhiteSpace(query.Isrc) && string.Equals(query.Isrc, candidate.Isrc, StringComparison.OrdinalIgnoreCase))
            total = Math.Max(total, 0.98);

        // Un titre sans rapport ne peut pas être sauvé par l'artiste et la durée.
        if (candidate.TitleScore < 0.4) total = Math.Min(total, 0.45);

        candidate.Score = Math.Round(Math.Clamp(total, 0, 1), 4);
    }

    public static double TitleScore(string sourceTitle, string candidateTitle)
    {
        var a = TextNormalizer.CoreTitle(sourceTitle);
        var b = TextNormalizer.CoreTitle(candidateTitle);
        if (a.Length == 0 || b.Length == 0) return 0;

        double score;
        if (a == b) score = 1;
        else
        {
            score = Math.Max(StringSimilarity.RatioNormalized(a, b), StringSimilarity.TokenSetRatio(a, b));
            // Inclusion complète (ex. titre YouTube contenant encore des mots parasites)
            if (score < 0.9 && (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal)))
            {
                var ratio = (double)Math.Min(a.Length, b.Length) / Math.Max(a.Length, b.Length);
                score = Math.Max(score, 0.75 + 0.2 * ratio);
            }
        }

        // Pénalité si les versions diffèrent (live, remix, acoustic...)
        var va = TextNormalizer.VersionTags(sourceTitle);
        var vb = TextNormalizer.VersionTags(candidateTitle);
        if (!va.SetEquals(vb))
        {
            score -= 0.15;
        }
        return Math.Clamp(score, 0, 1);
    }

    public static double ArtistScore(MatchQuery query, IReadOnlyList<string> candidateArtists)
    {
        if (candidateArtists.Count == 0) return 0;
        if (query.Artists.Count == 0)
        {
            // Pas d'artiste identifié : on regarde si l'artiste Spotify apparaît dans le titre brut.
            var raw = TextNormalizer.Normalize(query.RawSourceTitle ?? query.Title);
            return candidateArtists.Any(c => raw.Contains(TextNormalizer.Normalize(c), StringComparison.Ordinal)) ? 0.85 : 0.3;
        }

        var normalizedCandidates = candidateArtists.Select(TextNormalizer.Normalize).ToList();
        double sum = 0;
        foreach (var src in query.Artists)
        {
            var ns = TextNormalizer.Normalize(src);
            var best = normalizedCandidates.Count == 0 ? 0 : normalizedCandidates.Max(c => Math.Max(StringSimilarity.RatioNormalized(ns, c), StringSimilarity.TokenSetRatio(ns, c)));
            sum += best;
        }
        var primary = sum / query.Artists.Count;

        // L'artiste principal compte davantage.
        var mainSrc = TextNormalizer.Normalize(query.Artists[0]);
        var mainBest = normalizedCandidates.Max(c => Math.Max(StringSimilarity.RatioNormalized(mainSrc, c), StringSimilarity.TokenSetRatio(mainSrc, c)));
        return Math.Clamp(0.6 * mainBest + 0.4 * primary, 0, 1);
    }

    public static double DurationScore(long? sourceMs, long candidateMs)
    {
        if (sourceMs is null or <= 0 || candidateMs <= 0) return 0.5;
        var diff = Math.Abs(sourceMs.Value - candidateMs) / 1000.0;
        return diff switch
        {
            <= 2 => 1.0,
            <= 5 => 0.9,
            <= 10 => 0.7,
            <= 20 => 0.45,
            <= 40 => 0.2,
            _ => 0.0
        };
    }

    /// <summary>Score, trie et classe les candidats.</summary>
    public static MatchDecision Decide(MatchQuery query, IEnumerable<SpotifyCandidate> candidates, MatchThresholds? thresholds = null)
    {
        thresholds ??= MatchThresholds.Default;
        var list = candidates
            .GroupBy(c => c.SpotifyId)
            .Select(g => g.First())
            .ToList();
        foreach (var c in list) Score(query, c);
        var ranked = list.OrderByDescending(c => c.Score).ToList();

        if (ranked.Count == 0)
            return new MatchDecision(MatchStatus.NoMatch, null, ranked, "Aucun résultat Spotify.");

        var best = ranked[0];
        if (best.Score < thresholds.Minimum)
            return new MatchDecision(MatchStatus.NoMatch, null, ranked, $"Meilleur score trop faible ({best.Score:P0}).");

        if (best.Score >= thresholds.AutoAccept)
        {
            var rivals = ranked.Skip(1)
                .Where(c => best.Score - c.Score < thresholds.AmbiguityGap && !AreEquivalent(best, c))
                .ToList();
            if (rivals.Count == 0)
                return new MatchDecision(MatchStatus.AutoMatched, best, ranked, $"Correspondance fiable ({best.Score:P0}).");
            return new MatchDecision(MatchStatus.Ambiguous, best, ranked, $"{rivals.Count + 1} résultats très proches : validation manuelle requise.");
        }

        return new MatchDecision(MatchStatus.Ambiguous, best, ranked, $"Score intermédiaire ({best.Score:P0}) : validation manuelle requise.");
    }

    /// <summary>
    /// Deux candidats sont équivalents s'ils représentent le même enregistrement (même ISRC, ou même titre,
    /// mêmes artistes et durée quasi identique — typiquement single vs album).
    /// </summary>
    public static bool AreEquivalent(SpotifyCandidate a, SpotifyCandidate b)
    {
        if (!string.IsNullOrEmpty(a.Isrc) && string.Equals(a.Isrc, b.Isrc, StringComparison.OrdinalIgnoreCase)) return true;
        var sameTitle = TextNormalizer.Normalize(a.Title) == TextNormalizer.Normalize(b.Title);
        var sameArtists = a.Artists.Select(TextNormalizer.Normalize).ToHashSet().SetEquals(b.Artists.Select(TextNormalizer.Normalize));
        var sameDuration = Math.Abs(a.DurationMs - b.DurationMs) <= 2000;
        return sameTitle && sameArtists && sameDuration;
    }
}

using Microsoft.Extensions.Logging;
using Musilib.Application.Abstractions;
using Musilib.Domain.Entities;
using Musilib.Domain.Enums;
using Musilib.Domain.Services;

namespace Musilib.Application.Services;

/// <summary>
/// Recherche des correspondances Spotify, calcule les scores et persiste résultats et décisions.
/// Les recherches sont mises en cache (SQLite) afin d'éviter les appels API inutiles.
/// </summary>
public sealed class MatchingService
{
    private readonly ISpotifyClient _spotify;
    private readonly ITrackRepository _tracks;
    private readonly ISearchCache _cache;
    private readonly ISettingsService _settings;
    private readonly IHistoryRepository _history;
    private readonly ILogger<MatchingService> _logger;

    public MatchingService(ISpotifyClient spotify, ITrackRepository tracks, ISearchCache cache, ISettingsService settings,
        IHistoryRepository history, ILogger<MatchingService> logger)
    {
        _spotify = spotify;
        _tracks = tracks;
        _cache = cache;
        _settings = settings;
        _history = history;
        _logger = logger;
    }

    public static MatchQuery BuildQuery(Track track) => new(
        track.Metadata.Title,
        track.Metadata.Artists,
        track.SourceDurationMs ?? track.Metadata.DurationMs,
        track.MetadataEdited ? track.Metadata.Isrc : null,
        track.SourceTitle);

    /// <summary>Requêtes de recherche, de la plus précise à la plus large.</summary>
    public static IReadOnlyList<string> BuildSearchQueries(Track track)
    {
        var queries = new List<string>();
        if (!string.IsNullOrWhiteSpace(track.CustomSearchQuery))
        {
            queries.Add(track.CustomSearchQuery.Trim());
            return queries;
        }

        if (!string.IsNullOrWhiteSpace(track.Metadata.Isrc) && track.MetadataEdited)
            queries.Add($"isrc:{track.Metadata.Isrc.Trim()}");

        var title = TextNormalizer.CoreTitle(track.Metadata.Title);
        if (title.Length == 0) title = TextNormalizer.Normalize(track.SourceTitle);
        var artist = track.Metadata.Artists.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(artist))
        {
            queries.Add($"track:{Quote(title)} artist:{Quote(TextNormalizer.Normalize(artist))}");
            queries.Add($"{TextNormalizer.Normalize(artist)} {title}");
        }
        else
        {
            queries.Add(title);
        }
        return queries.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;

    /// <summary>Recherche et classe les correspondances pour un morceau, puis sauvegarde le résultat.</summary>
    public async Task<MatchDecision> MatchTrackAsync(Track track, bool force, CancellationToken ct)
    {
        if (!force && track.MatchStatus is MatchStatus.Confirmed)
        {
            var saved = await _tracks.GetCandidatesAsync(track.Id, ct).ConfigureAwait(false);
            return new MatchDecision(MatchStatus.Confirmed, saved.FirstOrDefault(c => c.SpotifyId == track.SpotifyTrackId), saved, "Déjà confirmé.");
        }

        // Vidéo supprimée/privée sans métadonnées exploitables : aucune requête inutile.
        if (!track.IsAvailable && !track.MetadataEdited && string.IsNullOrWhiteSpace(track.CustomSearchQuery))
        {
            track.ClearMatch(MatchStatus.NoMatch);
            track.LastError = track.RemovedFromSource
                ? "Morceau retiré de la playlist source"
                : PlaylistImportService.DescribeAvailability(track.Availability) + " : saisissez les métadonnées pour relancer la recherche.";
            await _tracks.SaveCandidatesAsync(track.Id, Array.Empty<SpotifyCandidate>(), ct).ConfigureAwait(false);
            await _tracks.UpdateAsync(track, ct).ConfigureAwait(false);
            return new MatchDecision(MatchStatus.NoMatch, null, Array.Empty<SpotifyCandidate>(), track.LastError);
        }

        var settings = _settings.Current.Matching;
        var reference = BuildQuery(track);
        var all = new List<SpotifyCandidate>();
        MatchDecision? decision = null;

        foreach (var q in BuildSearchQueries(track))
        {
            var results = await SearchCachedAsync(q, ct).ConfigureAwait(false);
            all.AddRange(results.Select(CloneCandidate));
            decision = MatchScorer.Decide(reference, all, settings.ToThresholds());
            // Correspondance fiable : inutile de lancer les requêtes plus larges.
            if (decision.Status == MatchStatus.AutoMatched) break;
        }
        decision ??= MatchScorer.Decide(reference, all, settings.ToThresholds());

        var top = decision.Ranked.Take(10).ToList();
        await _tracks.SaveCandidatesAsync(track.Id, top, ct).ConfigureAwait(false);

        if (decision.Status == MatchStatus.AutoMatched && decision.Best is not null)
        {
            track.ApplyCandidate(decision.Best, MatchStatus.AutoMatched, settings.ApplySpotifyMetadata);
        }
        else
        {
            // Jamais de validation silencieuse : la correspondance reste à choisir.
            track.ClearMatch(decision.Status);
            track.MatchScore = decision.Best?.Score ?? decision.Ranked.FirstOrDefault()?.Score;
            track.LastError = decision.Status == MatchStatus.NoMatch && !track.IsAvailable
                ? PlaylistImportService.DescribeAvailability(track.Availability)
                : null;
        }
        await _tracks.UpdateAsync(track, ct).ConfigureAwait(false);
        return decision;
    }

    /// <summary>Recherche manuelle (requête libre) ; les résultats sont scorés par rapport au morceau.</summary>
    public async Task<IReadOnlyList<SpotifyCandidate>> ManualSearchAsync(Track track, string query, CancellationToken ct)
    {
        var results = await SearchCachedAsync(query, ct).ConfigureAwait(false);
        var decision = MatchScorer.Decide(BuildQuery(track), results.Select(CloneCandidate), _settings.Current.Matching.ToThresholds());
        // On fusionne avec les candidats existants pour pouvoir comparer.
        var existing = await _tracks.GetCandidatesAsync(track.Id, ct).ConfigureAwait(false);
        var merged = decision.Ranked.Concat(existing.Where(e => decision.Ranked.All(r => r.SpotifyId != e.SpotifyId)))
            .OrderByDescending(c => c.Score).Take(20).ToList();
        await _tracks.SaveCandidatesAsync(track.Id, merged, ct).ConfigureAwait(false);
        return decision.Ranked;
    }

    /// <summary>Sélection manuelle d'une correspondance (statut Confirmé).</summary>
    public async Task SelectCandidateAsync(Track track, SpotifyCandidate candidate, bool applyMetadata, CancellationToken ct)
    {
        track.ApplyCandidate(candidate, MatchStatus.Confirmed, applyMetadata);
        await _tracks.UpdateAsync(track, ct).ConfigureAwait(false);
        await _history.AddAsync(new HistoryEntry
        {
            Kind = HistoryKind.Match,
            PlaylistId = track.PlaylistId,
            Title = $"Correspondance choisie : {track.Metadata.ArtistsDisplay} - {track.Metadata.Title}",
            Details = $"{candidate.ArtistsDisplay} - {candidate.Title} ({candidate.Album}) · score {candidate.Score:P0}"
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Confirme une correspondance automatique.</summary>
    public async Task ConfirmAsync(Track track, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(track.SpotifyUri)) return;
        track.MatchStatus = MatchStatus.Confirmed;
        track.Touch();
        await _tracks.UpdateAsync(track, ct).ConfigureAwait(false);
    }

    /// <summary>Indique qu'aucun résultat ne correspond.</summary>
    public async Task RejectAsync(Track track, CancellationToken ct)
    {
        track.ClearMatch(MatchStatus.NoMatch);
        await _tracks.UpdateAsync(track, ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<SpotifyCandidate>> GetCandidatesAsync(Track track, CancellationToken ct) =>
        _tracks.GetCandidatesAsync(track.Id, ct);

    private async Task<IReadOnlyList<SpotifyCandidate>> SearchCachedAsync(string query, CancellationToken ct)
    {
        var limit = Math.Clamp(_settings.Current.Matching.SearchLimit, 1, 50);
        var key = $"track|{limit}|{TextNormalizer.Normalize(query)}|{query.Trim().ToLowerInvariant()}";
        var maxAge = TimeSpan.FromDays(Math.Max(0, _settings.Current.Processing.SearchCacheDays));
        if (maxAge > TimeSpan.Zero)
        {
            var cached = await _cache.TryGetAsync(key, maxAge, ct).ConfigureAwait(false);
            if (cached is not null)
            {
                _logger.LogDebug("Recherche Spotify servie par le cache");
                return cached;
            }
        }
        var results = await _spotify.SearchTracksAsync(query, limit, 0, ct).ConfigureAwait(false);
        await _cache.SetAsync(key, results, ct).ConfigureAwait(false);
        return results;
    }

    private static SpotifyCandidate CloneCandidate(SpotifyCandidate c) => new()
    {
        SpotifyId = c.SpotifyId,
        Uri = c.Uri,
        Title = c.Title,
        Artists = new List<string>(c.Artists),
        AlbumArtists = new List<string>(c.AlbumArtists),
        Album = c.Album,
        AlbumId = c.AlbumId,
        ReleaseDate = c.ReleaseDate,
        DurationMs = c.DurationMs,
        Isrc = c.Isrc,
        CoverUrl = c.CoverUrl,
        PreviewUrl = c.PreviewUrl,
        ExternalUrl = c.ExternalUrl,
        Explicit = c.Explicit,
        TrackNumber = c.TrackNumber,
        DiscNumber = c.DiscNumber
    };
}

using Microsoft.Extensions.Logging;
using Musilib.Application.Abstractions;
using Musilib.Application.Errors;
using Musilib.Domain.Entities;
using Musilib.Domain.Enums;

namespace Musilib.Application.Services;

public enum TransferItemState
{
    Ready,
    NeedsReview,
    NoMatch,
    Excluded,
    Unavailable
}

public sealed record TransferPlanItem(Track Track, TransferItemState State, string Note);

public sealed class TransferPlan
{
    public TransferPlan(IReadOnlyList<TransferPlanItem> items) => Items = items;
    public IReadOnlyList<TransferPlanItem> Items { get; }
    public int Ready => Items.Count(i => i.State == TransferItemState.Ready);
    public int NeedsReview => Items.Count(i => i.State == TransferItemState.NeedsReview);
    public int NoMatch => Items.Count(i => i.State == TransferItemState.NoMatch);
    public int Excluded => Items.Count(i => i.State is TransferItemState.Excluded);
    public int Unavailable => Items.Count(i => i.State is TransferItemState.Unavailable);
}

/// <summary>Paramètres d'un transfert vers Spotify.</summary>
public sealed class TransferRequest
{
    public Guid PlaylistId { get; set; }
    /// <summary>Playlist Spotify existante ; si nul, une nouvelle playlist est créée.</summary>
    public string? ExistingPlaylistId { get; set; }
    public string? ExistingPlaylistName { get; set; }
    public string? NewPlaylistName { get; set; }
    public string? NewPlaylistDescription { get; set; }
    public bool IsPublic { get; set; }
    /// <summary>N'ajoute pas les morceaux déjà présents dans la playlist cible.</summary>
    public bool SkipExisting { get; set; } = true;
    /// <summary>Morceaux sélectionnés (l'ordre d'envoi suit toujours la position d'origine).</summary>
    public List<Guid> TrackIds { get; set; } = new();
}

public sealed record TransferLine(Guid TrackId, string Label, string? Reason = null);

/// <summary>État persistant du transfert : permet la reprise sans recréer la playlist ni dupliquer les ajouts.</summary>
public sealed class TransferState
{
    public string? TargetPlaylistId { get; set; }
    public string? TargetPlaylistName { get; set; }
    public string? TargetPlaylistUrl { get; set; }
    public bool Created { get; set; }
    public List<TransferLine> Added { get; set; } = new();
    public List<TransferLine> Skipped { get; set; } = new();
    public List<TransferLine> Failed { get; set; } = new();
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>Transfert ordonné de morceaux vers une playlist Spotify (lots de 100, reprise, échecs partiels).</summary>
public sealed class TransferService
{
    private readonly ISpotifyClient _spotify;
    private readonly ITrackRepository _tracks;
    private readonly ILogger<TransferService> _logger;

    public TransferService(ISpotifyClient spotify, ITrackRepository tracks, ILogger<TransferService> logger)
    {
        _spotify = spotify;
        _tracks = tracks;
        _logger = logger;
    }

    public static TransferPlan BuildPlan(IEnumerable<Track> tracks)
    {
        var items = tracks.OrderBy(t => t.Position).Select(t =>
        {
            if (t.Excluded) return new TransferPlanItem(t, TransferItemState.Excluded, "Exclu par l'utilisateur");
            if (t.HasSpotifyMatch) return new TransferPlanItem(t, TransferItemState.Ready,
                t.MatchStatus == MatchStatus.Confirmed ? "Confirmé" : $"Automatique ({t.MatchScore:P0})");
            if (t.MatchStatus == MatchStatus.Ambiguous) return new TransferPlanItem(t, TransferItemState.NeedsReview, "Correspondance ambiguë à valider");
            if (!t.IsAvailable && t.MatchStatus != MatchStatus.Confirmed) return new TransferPlanItem(t, TransferItemState.Unavailable, PlaylistImportService.DescribeAvailability(t.Availability));
            return t.MatchStatus switch
            {
                MatchStatus.Pending => new TransferPlanItem(t, TransferItemState.NeedsReview, "Recherche non effectuée"),
                MatchStatus.Error => new TransferPlanItem(t, TransferItemState.NeedsReview, t.LastError ?? "Erreur de recherche"),
                _ => new TransferPlanItem(t, TransferItemState.NoMatch, "Aucune correspondance")
            };
        }).ToList();
        return new TransferPlan(items);
    }

    public static string Label(Track t) => $"{t.Metadata.ArtistsDisplay} - {t.Metadata.Title}";

    public async Task ExecuteAsync(
        TransferRequest request,
        TransferState state,
        Func<TransferState, Task> saveState,
        Action<int, int, string?> report,
        CancellationToken ct)
    {
        state.StartedAt ??= DateTimeOffset.UtcNow;

        // 1. Playlist cible — l'identifiant est sauvegardé immédiatement pour éviter les doublons à la reprise.
        if (string.IsNullOrEmpty(state.TargetPlaylistId))
        {
            if (!string.IsNullOrEmpty(request.ExistingPlaylistId))
            {
                state.TargetPlaylistId = request.ExistingPlaylistId;
                state.TargetPlaylistName = request.ExistingPlaylistName;
                state.TargetPlaylistUrl = $"https://open.spotify.com/playlist/{request.ExistingPlaylistId}";
            }
            else
            {
                var name = string.IsNullOrWhiteSpace(request.NewPlaylistName) ? "Playlist Musilib" : request.NewPlaylistName.Trim();
                var created = await _spotify.CreatePlaylistAsync(name, request.NewPlaylistDescription, request.IsPublic, ct).ConfigureAwait(false);
                state.TargetPlaylistId = created.Id;
                state.TargetPlaylistName = created.Name;
                state.TargetPlaylistUrl = created.ExternalUrl ?? $"https://open.spotify.com/playlist/{created.Id}";
                state.Created = true;
            }
            await saveState(state).ConfigureAwait(false);
        }

        // 2. Sélection, dans l'ordre d'origine.
        var selected = new HashSet<Guid>(request.TrackIds);
        var all = await _tracks.GetByPlaylistAsync(request.PlaylistId, ct).ConfigureAwait(false);
        var ordered = all.Where(t => selected.Count == 0 || selected.Contains(t.Id)).OrderBy(t => t.Position).ToList();

        var done = new HashSet<Guid>(state.Added.Select(a => a.TrackId).Concat(state.Skipped.Select(s => s.TrackId)));
        // Les échecs précédents sont retentés lors d'une reprise.
        state.Failed.Clear();

        var pending = new List<Track>();
        foreach (var t in ordered)
        {
            if (done.Contains(t.Id)) continue;
            if (t.Excluded) { state.Skipped.Add(new TransferLine(t.Id, Label(t), "Exclu")); continue; }
            if (!t.HasSpotifyMatch)
            {
                state.Skipped.Add(new TransferLine(t.Id, Label(t), t.MatchStatus == MatchStatus.Ambiguous ? "Correspondance non validée" : "Aucune correspondance"));
                continue;
            }
            pending.Add(t);
        }

        // 3. Doublons déjà présents dans la playlist cible.
        if (request.SkipExisting && !state.Created && pending.Count > 0)
        {
            try
            {
                var existing = new HashSet<string>(await _spotify.GetPlaylistItemUrisAsync(state.TargetPlaylistId!, ct).ConfigureAwait(false), StringComparer.Ordinal);
                foreach (var t in pending.Where(t => existing.Contains(t.SpotifyUri!)).ToList())
                {
                    state.Skipped.Add(new TransferLine(t.Id, Label(t), "Déjà présent dans la playlist"));
                    pending.Remove(t);
                }
            }
            catch (ApiException ex) when (!ex.IsTransient)
            {
                _logger.LogWarning("Lecture du contenu de la playlist cible impossible ({Status}) ; détection des doublons ignorée", ex.StatusCode);
            }
        }

        // Doublons à l'intérieur de la sélection.
        var uris = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in pending.ToList())
        {
            if (!uris.Add(t.SpotifyUri!))
            {
                state.Skipped.Add(new TransferLine(t.Id, Label(t), "Doublon dans la sélection"));
                pending.Remove(t);
            }
        }
        await saveState(state).ConfigureAwait(false);

        var total = state.Added.Count + state.Skipped.Count + pending.Count;
        report(state.Added.Count + state.Skipped.Count, total, null);

        // 4. Envoi par lots de 100, séquentiel pour garantir l'ordre.
        foreach (var chunk in pending.Chunk(ISpotifyClient.MaxItemsPerAddRequest))
        {
            ct.ThrowIfCancellationRequested();
            report(state.Added.Count + state.Skipped.Count, total, Label(chunk[0]));
            try
            {
                await _spotify.AddItemsAsync(state.TargetPlaylistId!, chunk.Select(t => t.SpotifyUri!).ToList(), ct).ConfigureAwait(false);
                state.Added.AddRange(chunk.Select(t => new TransferLine(t.Id, Label(t))));
            }
            catch (ApiException ex) when (!ex.IsTransient && ex is not QuotaExceededException)
            {
                // Lot refusé (ex. URI invalide) : on isole les éléments fautifs un par un.
                _logger.LogWarning("Lot refusé par Spotify ({Status}), envoi unitaire", ex.StatusCode);
                foreach (var t in chunk)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        await _spotify.AddItemsAsync(state.TargetPlaylistId!, new[] { t.SpotifyUri! }, ct).ConfigureAwait(false);
                        state.Added.Add(new TransferLine(t.Id, Label(t)));
                    }
                    catch (ApiException single) when (!single.IsTransient && single is not QuotaExceededException)
                    {
                        state.Failed.Add(new TransferLine(t.Id, Label(t), single.Message));
                    }
                    await saveState(state).ConfigureAwait(false);
                }
            }
            await saveState(state).ConfigureAwait(false);
            report(state.Added.Count + state.Skipped.Count + state.Failed.Count, total, null);
        }

        state.FinishedAt = DateTimeOffset.UtcNow;
        await saveState(state).ConfigureAwait(false);
    }
}

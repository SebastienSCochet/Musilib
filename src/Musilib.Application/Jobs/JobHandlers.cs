using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Musilib.Application.Abstractions;
using Musilib.Application.Errors;
using Musilib.Application.Services;
using Musilib.Domain.Entities;
using Musilib.Domain.Enums;

namespace Musilib.Application.Jobs;

public sealed class ImportJobPayload
{
    public string ExternalPlaylistId { get; set; } = string.Empty;
    /// <summary>Enchaîne automatiquement la recherche Spotify après l'import.</summary>
    public bool AutoMatch { get; set; } = true;
}

public sealed class MatchJobPayload
{
    public Guid PlaylistId { get; set; }
    /// <summary>Morceaux ciblés ; vide = tous les morceaux à analyser.</summary>
    public List<Guid> TrackIds { get; set; } = new();
    /// <summary>Relance la recherche même pour les morceaux déjà analysés (hors confirmés).</summary>
    public bool Force { get; set; }
}

public sealed class ExportJobState
{
    public Dictionary<Guid, string> ExportedPaths { get; set; } = new();
    public string? M3uPath { get; set; }
}

public sealed class ImportJobHandler : IJobHandler
{
    private readonly PlaylistImportService _import;
    private readonly IServiceProvider _services;

    public ImportJobHandler(PlaylistImportService import, IServiceProvider services)
    {
        _import = import;
        _services = services;
    }

    public JobType Type => JobType.Import;

    public async Task<JobOutcome> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        var payload = context.GetPayload<ImportJobPayload>();
        var progress = new SyncProgress<ImportProgress>(p => _ = context.ReportAsync(p.Processed, p.Total, p.CurrentTitle, CancellationToken.None));
        var result = await _import.ImportAsync(payload.ExternalPlaylistId, progress, ct).ConfigureAwait(false);
        context.Job.PlaylistId = result.Playlist.Id;
        await context.ReportAsync(result.Playlist.SourceItemCount, result.Playlist.SourceItemCount, null, ct).ConfigureAwait(false);

        if (payload.AutoMatch)
        {
            var queue = _services.GetRequiredService<IJobQueue>();
            await queue.EnqueueAsync(JobType.Match, result.Playlist.Id, $"Recherche Spotify · {result.Playlist.Title}",
                new MatchJobPayload { PlaylistId = result.Playlist.Id }, ct).ConfigureAwait(false);
        }

        var msg = $"{result.Playlist.SourceItemCount} morceaux · {result.Added} nouveaux · {result.Unavailable} indisponibles";
        return JobOutcome.Completed(msg);
    }
}

public sealed class MatchJobHandler : IJobHandler
{
    /// <summary>Au-delà de ce nombre d'échecs consécutifs (réseau coupé...), le traitement s'interrompt pour être repris.</summary>
    public const int MaxConsecutiveFailures = 5;

    private readonly MatchingService _matching;
    private readonly ITrackRepository _tracks;
    private readonly ILogger<MatchJobHandler> _logger;

    public MatchJobHandler(MatchingService matching, ITrackRepository tracks, ILogger<MatchJobHandler> logger)
    {
        _matching = matching;
        _tracks = tracks;
        _logger = logger;
    }

    public JobType Type => JobType.Match;

    public async Task<JobOutcome> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        var payload = context.GetPayload<MatchJobPayload>();
        var tracks = await _tracks.GetByPlaylistAsync(payload.PlaylistId, ct).ConfigureAwait(false);
        var selected = payload.TrackIds.Count > 0 ? payload.TrackIds.ToHashSet() : null;

        var targets = tracks
            .Where(t => selected is null || selected.Contains(t.Id))
            .Where(t => !t.Excluded)
            .Where(t => payload.Force || selected is not null
                ? t.MatchStatus != MatchStatus.Confirmed
                : t.MatchStatus is MatchStatus.Pending or MatchStatus.Error)
            .OrderBy(t => t.Position)
            .ToList();

        await context.SetTotalAsync(targets.Count, ct).ConfigureAwait(false);
        var consecutiveFailures = 0;
        int auto = 0, ambiguous = 0, none = 0, failed = 0;
        Exception? fatal = null;

        try
        {
            await RunAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (fatal is not null && !ct.IsCancellationRequested)
        {
            // Interruption volontaire suite aux échecs consécutifs.
        }

        var summary = $"{auto} fiables · {ambiguous} à vérifier · {none} sans résultat";
        if (fatal is not null) return JobOutcome.Failed($"Interrompu après {MaxConsecutiveFailures} échecs consécutifs ({fatal.Message}). Reprenez le traitement une fois la connexion rétablie.");
        return failed > 0 ? JobOutcome.Partial($"{summary} · {failed} erreurs") : JobOutcome.Completed(summary);

        Task RunAsync() => Parallel.ForEachAsync(targets.Where(t => !context.IsItemDone(t.Id.ToString())),
            new ParallelOptions { MaxDegreeOfParallelism = context.MaxDegreeOfParallelism, CancellationToken = ct },
            async (track, token) =>
            {
                context.SetCurrent($"{track.Metadata.ArtistsDisplay} - {track.Metadata.Title}");
                try
                {
                    var decision = await _matching.MatchTrackAsync(track, payload.Force, token).ConfigureAwait(false);
                    Interlocked.Exchange(ref consecutiveFailures, 0);
                    switch (decision.Status)
                    {
                        case MatchStatus.AutoMatched: Interlocked.Increment(ref auto); break;
                        case MatchStatus.Ambiguous: Interlocked.Increment(ref ambiguous); break;
                        case MatchStatus.NoMatch: Interlocked.Increment(ref none); break;
                    }
                    await context.CompleteItemAsync(track.Id.ToString(), JobItemStatus.Completed, decision.Reason, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is ApiException and not QuotaExceededException || ex is HttpRequestException || ex is TimeoutException)
                {
                    Interlocked.Increment(ref failed);
                    track.MatchStatus = MatchStatus.Error;
                    track.LastError = ex.Message;
                    track.Touch();
                    await _tracks.UpdateAsync(track, CancellationToken.None).ConfigureAwait(false);
                    await context.CompleteItemAsync(track.Id.ToString(), JobItemStatus.Failed, ex.Message, CancellationToken.None).ConfigureAwait(false);
                    _logger.LogWarning("Recherche impossible pour le morceau {TrackId} : {Message}", track.Id, ex.Message);
                    if (Interlocked.Increment(ref consecutiveFailures) >= MaxConsecutiveFailures)
                    {
                        fatal = ex;
                        throw new OperationCanceledException("Trop d'échecs consécutifs");
                    }
                }
            });
    }
}

public sealed class ExportJobHandler : IJobHandler
{
    private readonly ExportService _export;
    private readonly ITrackRepository _tracks;

    public ExportJobHandler(ExportService export, ITrackRepository tracks)
    {
        _export = export;
        _tracks = tracks;
    }

    public JobType Type => JobType.Export;

    public async Task<JobOutcome> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        var options = context.GetPayload<ExportOptions>();
        if (string.IsNullOrWhiteSpace(options.DestinationFolder)) throw new ConfigurationMissingException("Aucun dossier de destination sélectionné.");
        Directory.CreateDirectory(options.DestinationFolder);

        var state = context.GetState<ExportJobState>();
        var all = await _tracks.GetByPlaylistAsync(options.PlaylistId, ct).ConfigureAwait(false);
        var selected = options.TrackIds.Count > 0 ? options.TrackIds.ToHashSet() : null;
        var tracks = all.Where(t => selected is null || selected.Contains(t.Id)).ToList();

        var pending = tracks.Where(t => !context.IsItemDone(t.Id.ToString())).ToList();
        var plan = await _export.PlanAsync(pending, options, ct).ConfigureAwait(false);
        await context.SetTotalAsync(tracks.Count, ct).ConfigureAwait(false);

        var gate = new object();
        int exported = 0, skipped = 0, failed = 0;
        await Parallel.ForEachAsync(plan, new ParallelOptions { MaxDegreeOfParallelism = context.MaxDegreeOfParallelism, CancellationToken = ct },
            async (item, token) =>
            {
                context.SetCurrent($"{item.Track.Metadata.ArtistsDisplay} - {item.Track.Metadata.Title}");
                ExportItemResult result;
                try
                {
                    result = await _export.ExportItemAsync(item, options, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
                {
                    result = new ExportItemResult(item.Track.Id, JobItemStatus.Failed, null, ex.Message);
                }
                switch (result.Status)
                {
                    case JobItemStatus.Completed: Interlocked.Increment(ref exported); break;
                    case JobItemStatus.Skipped: Interlocked.Increment(ref skipped); break;
                    case JobItemStatus.Failed: Interlocked.Increment(ref failed); break;
                }
                if (result.TargetPath is not null && result.Status != JobItemStatus.Failed && File.Exists(result.TargetPath))
                {
                    lock (gate) state.ExportedPaths[item.Track.Id] = result.TargetPath;
                }
                await context.CompleteItemAsync(item.Track.Id.ToString(), result.Status, result.Message, CancellationToken.None).ConfigureAwait(false);
            }).ConfigureAwait(false);

        if (options.WriteM3u && state.ExportedPaths.Count > 0)
        {
            var byId = tracks.ToDictionary(t => t.Id);
            var entries = state.ExportedPaths.Where(kv => byId.ContainsKey(kv.Key)).Select(kv => (byId[kv.Key], kv.Value));
            state.M3uPath = await ExportService.WriteM3uAsync(options.DestinationFolder, options.PlaylistName ?? "Playlist", entries, ct).ConfigureAwait(false);
        }
        await context.SaveStateAsync(state, ct).ConfigureAwait(false);

        var summary = $"{exported} exportés · {skipped} ignorés · {failed} erreurs";
        return failed > 0 ? (exported > 0 ? JobOutcome.Partial(summary) : JobOutcome.Failed(summary)) : JobOutcome.Completed(summary);
    }
}

public sealed class TransferJobHandler : IJobHandler
{
    private readonly TransferService _transfer;

    public TransferJobHandler(TransferService transfer) => _transfer = transfer;

    public JobType Type => JobType.Transfer;

    public async Task<JobOutcome> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        var request = context.GetPayload<TransferRequest>();
        var state = context.GetState<TransferState>();
        await _transfer.ExecuteAsync(
            request,
            state,
            s => context.SaveStateAsync(s, CancellationToken.None),
            (done, total, current) => _ = context.ReportAsync(done, total, current, CancellationToken.None),
            ct).ConfigureAwait(false);

        await context.ReportAsync(state.Added.Count + state.Skipped.Count + state.Failed.Count,
            state.Added.Count + state.Skipped.Count + state.Failed.Count, null, ct).ConfigureAwait(false);
        context.Job.Failed = state.Failed.Count;

        var summary = $"« {state.TargetPlaylistName} » : {state.Added.Count} ajoutés · {state.Skipped.Count} ignorés · {state.Failed.Count} échecs";
        if (state.Failed.Count > 0) return state.Added.Count > 0 ? JobOutcome.Partial(summary) : JobOutcome.Failed(summary);
        return JobOutcome.Completed(summary);
    }
}

/// <summary>IProgress synchrone (évite la capture du contexte de synchronisation).</summary>
internal sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;
    public SyncProgress(Action<T> handler) => _handler = handler;
    public void Report(T value) => _handler(value);
}

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Musilib.Application.Abstractions;
using Musilib.Application.Common;
using Musilib.Application.Errors;
using Musilib.Domain.Entities;
using Musilib.Domain.Enums;

namespace Musilib.Application.Jobs;

/// <summary>
/// Moteur de traitement asynchrone : file d'attente persistée dans SQLite, nombre de traitements
/// concurrents configurable, annulation/pause propres et reprise automatique après fermeture.
/// </summary>
public sealed class JobEngine : IJobQueue, IAsyncDisposable
{
    private sealed class RunningJob
    {
        public required CancellationTokenSource Cts { get; init; }
        public JobStatus? RequestedStatus { get; set; }
        public Task? Task { get; set; }
    }

    private static readonly JobStatus[] ActiveStatuses = [JobStatus.Queued, JobStatus.Running, JobStatus.Paused];

    private readonly IJobRepository _jobs;
    private readonly IHistoryRepository _history;
    private readonly ISettingsService _settings;
    private readonly ILogger<JobEngine> _logger;
    private readonly Dictionary<JobType, IJobHandler> _handlers;
    private readonly ConcurrentDictionary<Guid, RunningJob> _running = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly SemaphoreSlim _dispatchLock = new(1, 1);
    private CancellationTokenSource? _shutdown;
    private Task? _loop;

    public JobEngine(IJobRepository jobs, IHistoryRepository history, IEnumerable<IJobHandler> handlers, ISettingsService settings, ILogger<JobEngine> logger)
    {
        _jobs = jobs;
        _history = history;
        _settings = settings;
        _logger = logger;
        _handlers = handlers.ToDictionary(h => h.Type);
    }

    public event EventHandler<Job>? JobChanged;

    public IReadOnlyCollection<Guid> RunningJobIds => _running.Keys.ToList();

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_loop is not null) return;

        // Reprise : les traitements interrompus brutalement (statut Running) sont remis en file.
        var interrupted = await _jobs.GetByStatusAsync([JobStatus.Running], ct).ConfigureAwait(false);
        foreach (var job in interrupted)
        {
            job.Status = _settings.Current.Processing.AutoResumeJobs ? JobStatus.Queued : JobStatus.Paused;
            job.Message = "Reprise après interruption";
            await _jobs.UpdateAsync(job, ct).ConfigureAwait(false);
        }
        if (interrupted.Count > 0) _logger.LogInformation("{Count} traitement(s) interrompu(s) récupéré(s)", interrupted.Count);

        _shutdown = new CancellationTokenSource();
        _loop = Task.Run(() => RunLoopAsync(_shutdown.Token));
        _signal.Release();
    }

    public async Task StopAsync()
    {
        if (_shutdown is null) return;
        // Les traitements en cours reprendront au prochain démarrage.
        foreach (var running in _running.Values)
        {
            running.RequestedStatus = JobStatus.Queued;
            running.Cts.Cancel();
        }
        await _shutdown.CancelAsync().ConfigureAwait(false);
        var tasks = _running.Values.Select(r => r.Task).OfType<Task>().ToList();
        if (_loop is not null) tasks.Add(_loop);
        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            // Arrêt best effort
        }
        _loop = null;
    }

    public async Task<Job> EnqueueAsync(JobType type, Guid? playlistId, string title, object payload, CancellationToken ct = default)
    {
        if (!_handlers.ContainsKey(type)) throw new InvalidOperationException($"Aucun gestionnaire pour {type}.");

        var payloadJson = JsonDefaults.Serialize(payload);
        // Évite les doublons (et donc les appels API inutiles) : même type, même playlist, mêmes paramètres.
        var active = await _jobs.GetByStatusAsync(ActiveStatuses, ct).ConfigureAwait(false);
        var duplicate = active.FirstOrDefault(j => j.Type == type && j.PlaylistId == playlistId && j.Payload == payloadJson);
        if (duplicate is not null)
        {
            if (duplicate.Status == JobStatus.Paused) await ResumeAsync(duplicate.Id).ConfigureAwait(false);
            return duplicate;
        }

        var job = new Job
        {
            Type = type,
            PlaylistId = playlistId,
            Title = title,
            Payload = payloadJson,
            Status = JobStatus.Queued
        };
        await _jobs.InsertAsync(job, ct).ConfigureAwait(false);
        Notify(job.Snapshot());
        _signal.Release();
        return job;
    }

    public async Task PauseAsync(Guid jobId)
    {
        if (_running.TryGetValue(jobId, out var running))
        {
            running.RequestedStatus = JobStatus.Paused;
            await running.Cts.CancelAsync().ConfigureAwait(false);
            return;
        }
        var job = await _jobs.GetAsync(jobId).ConfigureAwait(false);
        if (job is { Status: JobStatus.Queued })
        {
            job.Status = JobStatus.Paused;
            await _jobs.UpdateAsync(job).ConfigureAwait(false);
            Notify(job);
        }
    }

    public async Task CancelAsync(Guid jobId)
    {
        if (_running.TryGetValue(jobId, out var running))
        {
            running.RequestedStatus = JobStatus.Cancelled;
            await running.Cts.CancelAsync().ConfigureAwait(false);
            return;
        }
        var job = await _jobs.GetAsync(jobId).ConfigureAwait(false);
        if (job is not null && !job.IsTerminal)
        {
            job.Status = JobStatus.Cancelled;
            job.CompletedAt = DateTimeOffset.UtcNow;
            await _jobs.UpdateAsync(job).ConfigureAwait(false);
            Notify(job);
        }
    }

    public async Task ResumeAsync(Guid jobId)
    {
        if (_running.ContainsKey(jobId)) return;
        var job = await _jobs.GetAsync(jobId).ConfigureAwait(false);
        if (job is null || !(job.CanResume || job.Status == JobStatus.Queued)) return;
        job.Status = JobStatus.Queued;
        job.Message = null;
        job.CompletedAt = null;
        await _jobs.UpdateAsync(job).ConfigureAwait(false);
        Notify(job);
        _signal.Release();
    }

    public async Task RemoveAsync(Guid jobId)
    {
        if (_running.TryGetValue(jobId, out var running))
        {
            running.RequestedStatus = JobStatus.Cancelled;
            await running.Cts.CancelAsync().ConfigureAwait(false);
            if (running.Task is not null)
            {
                try { await running.Task.ConfigureAwait(false); } catch { /* déjà journalisé */ }
            }
        }
        await _jobs.DeleteAsync(jobId).ConfigureAwait(false);
    }

    public Task<int> ClearFinishedAsync() => _jobs.DeleteTerminatedAsync();

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                await DispatchAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur du répartiteur de traitements");
                try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task DispatchAsync(CancellationToken ct)
    {
        await _dispatchLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var max = Math.Clamp(_settings.Current.Processing.MaxConcurrentJobs, 1, 16);
            var slots = max - _running.Count;
            if (slots <= 0) return;

            var queued = await _jobs.GetByStatusAsync([JobStatus.Queued], ct).ConfigureAwait(false);
            foreach (var job in queued.Where(j => !_running.ContainsKey(j.Id)).OrderBy(j => j.CreatedAt).Take(slots))
            {
                var running = new RunningJob { Cts = CancellationTokenSource.CreateLinkedTokenSource(ct) };
                if (!_running.TryAdd(job.Id, running)) continue;
                running.Task = Task.Run(() => RunJobAsync(job, running));
            }
        }
        finally
        {
            _dispatchLock.Release();
        }
    }

    private async Task RunJobAsync(Job job, RunningJob running)
    {
        var token = running.Cts.Token;
        try
        {
            job.Status = JobStatus.Running;
            job.StartedAt ??= DateTimeOffset.UtcNow;
            job.Message = null;
            job.Failed = 0;
            await _jobs.UpdateAsync(job, CancellationToken.None).ConfigureAwait(false);
            Notify(job.Snapshot());

            var items = await _jobs.GetItemsAsync(job.Id, CancellationToken.None).ConfigureAwait(false);
            var context = new JobContext(job, _jobs, items, _settings.Current.Processing.MaxConcurrentRequests, Notify);
            var handler = _handlers[job.Type];

            JobOutcome outcome;
            try
            {
                outcome = await handler.ExecuteAsync(context, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                var status = running.RequestedStatus ?? JobStatus.Paused;
                outcome = new JobOutcome(status, status switch
                {
                    JobStatus.Cancelled => "Annulé par l'utilisateur",
                    JobStatus.Queued => "Interrompu : reprise au prochain démarrage",
                    _ => "En pause"
                });
            }
            catch (QuotaExceededException ex)
            {
                outcome = new JobOutcome(JobStatus.Paused, $"Quota {ex.Provider} atteint : reprenez plus tard. {ex.Message}");
            }
            catch (AuthenticationRequiredException ex)
            {
                outcome = new JobOutcome(JobStatus.Paused, $"Connexion {ex.Provider} requise : {ex.Message}");
            }
            catch (ConfigurationMissingException ex)
            {
                outcome = JobOutcome.Failed(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Échec du traitement {JobId} ({Type})", job.Id, job.Type);
                outcome = JobOutcome.Failed(ex is ApiException api ? $"{api.Provider} : {api.Message}" : ex.Message);
            }

            job.Status = outcome.Status;
            job.Message = outcome.Message;
            job.CurrentItem = null;
            if (job.IsTerminal) job.CompletedAt = DateTimeOffset.UtcNow;
            await _jobs.UpdateAsync(job, CancellationToken.None).ConfigureAwait(false);

            if (job.IsTerminal)
            {
                await _history.AddAsync(new HistoryEntry
                {
                    Kind = job.Type switch
                    {
                        JobType.Import => HistoryKind.Import,
                        JobType.Match => HistoryKind.Match,
                        JobType.Export => HistoryKind.Export,
                        _ => HistoryKind.Transfer
                    },
                    PlaylistId = job.PlaylistId,
                    Title = job.Title,
                    Details = job.Message,
                    Success = job.Status is JobStatus.Completed or JobStatus.PartiallyCompleted
                }, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur interne du moteur pour le traitement {JobId}", job.Id);
        }
        finally
        {
            _running.TryRemove(job.Id, out _);
            running.Cts.Dispose();
            Notify(job.Snapshot());
            try { _signal.Release(); } catch (ObjectDisposedException) { }
        }
    }

    private void Notify(Job job)
    {
        try { JobChanged?.Invoke(this, job); }
        catch (Exception ex) { _logger.LogWarning(ex, "Erreur d'un abonné aux notifications de traitement"); }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _shutdown?.Dispose();
    }
}

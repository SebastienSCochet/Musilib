using Musilib.Application.Abstractions;
using Musilib.Application.Common;
using Musilib.Domain.Entities;
using Musilib.Domain.Enums;

namespace Musilib.Application.Jobs;

public sealed record JobOutcome(JobStatus Status, string? Message)
{
    public static JobOutcome Completed(string? message = null) => new(JobStatus.Completed, message);
    public static JobOutcome Partial(string message) => new(JobStatus.PartiallyCompleted, message);
    public static JobOutcome Failed(string message) => new(JobStatus.Failed, message);
}

/// <summary>Exécute un type de traitement. Doit être idempotent : il peut être relancé après interruption.</summary>
public interface IJobHandler
{
    JobType Type { get; }
    Task<JobOutcome> ExecuteAsync(JobContext context, CancellationToken ct);
}

/// <summary>File d'attente persistante des traitements.</summary>
public interface IJobQueue
{
    event EventHandler<Job>? JobChanged;
    IReadOnlyCollection<Guid> RunningJobIds { get; }
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync();
    Task<Job> EnqueueAsync(JobType type, Guid? playlistId, string title, object payload, CancellationToken ct = default);
    Task PauseAsync(Guid jobId);
    Task CancelAsync(Guid jobId);
    Task ResumeAsync(Guid jobId);
    Task RemoveAsync(Guid jobId);
    Task<int> ClearFinishedAsync();
}

/// <summary>Contexte d'exécution : progression, état de reprise et statut par élément.</summary>
public sealed class JobContext
{
    private readonly IJobRepository _repository;
    private readonly Dictionary<string, JobItem> _items;
    private readonly Action<Job> _notify;
    private readonly object _gate = new();
    private DateTimeOffset _lastPersist = DateTimeOffset.MinValue;
    private DateTimeOffset _lastNotify = DateTimeOffset.MinValue;

    public JobContext(Job job, IJobRepository repository, IEnumerable<JobItem> items, int maxDegreeOfParallelism, Action<Job> notify)
    {
        Job = job;
        _repository = repository;
        _items = items.ToDictionary(i => i.ItemKey, StringComparer.Ordinal);
        _notify = notify;
        MaxDegreeOfParallelism = Math.Max(1, maxDegreeOfParallelism);
    }

    public Job Job { get; }
    public int MaxDegreeOfParallelism { get; }
    public TimeSpan PersistInterval { get; init; } = TimeSpan.FromMilliseconds(750);
    public TimeSpan NotifyInterval { get; init; } = TimeSpan.FromMilliseconds(150);

    public T GetPayload<T>() => JsonDefaults.Deserialize<T>(Job.Payload) ?? throw new InvalidOperationException("Paramètres du traitement invalides.");

    public T GetState<T>() where T : new() => JsonDefaults.Deserialize<T>(Job.State) ?? new T();

    public async Task SaveStateAsync<T>(T state, CancellationToken ct = default)
    {
        Job snapshot;
        lock (_gate)
        {
            Job.State = JsonDefaults.Serialize(state);
            snapshot = Job.Snapshot();
            _lastPersist = DateTimeOffset.UtcNow;
        }
        await _repository.UpdateAsync(snapshot, ct).ConfigureAwait(false);
        _notify(snapshot);
    }

    /// <summary>Vrai si l'élément a déjà été traité lors d'une exécution précédente.</summary>
    public bool IsItemDone(string key)
    {
        lock (_gate)
        {
            return _items.TryGetValue(key, out var item) && item.Status is JobItemStatus.Completed or JobItemStatus.Skipped;
        }
    }

    public int DoneCount
    {
        get { lock (_gate) return _items.Values.Count(i => i.Status is JobItemStatus.Completed or JobItemStatus.Skipped); }
    }

    public IReadOnlyList<JobItem> Items
    {
        get { lock (_gate) return _items.Values.ToList(); }
    }

    public async Task SetTotalAsync(int total, CancellationToken ct = default)
    {
        Job snapshot;
        lock (_gate)
        {
            Job.Total = total;
            Job.Processed = _items.Values.Count(i => i.Status is JobItemStatus.Completed or JobItemStatus.Skipped);
            Job.Failed = 0;
            snapshot = Job.Snapshot();
        }
        await _repository.UpdateAsync(snapshot, ct).ConfigureAwait(false);
        _notify(snapshot);
    }

    public void SetCurrent(string? label)
    {
        Job? snapshot = null;
        lock (_gate)
        {
            Job.CurrentItem = label;
            if (DateTimeOffset.UtcNow - _lastNotify >= NotifyInterval)
            {
                _lastNotify = DateTimeOffset.UtcNow;
                snapshot = Job.Snapshot();
            }
        }
        if (snapshot is not null) _notify(snapshot);
    }

    /// <summary>Progression libre (traitements sans éléments unitaires, ex. import).</summary>
    public async Task ReportAsync(int processed, int total, string? current, CancellationToken ct = default)
    {
        Job? persist = null;
        Job? notify = null;
        lock (_gate)
        {
            Job.Processed = processed;
            Job.Total = total;
            Job.CurrentItem = current;
            var now = DateTimeOffset.UtcNow;
            if (now - _lastPersist >= PersistInterval) { _lastPersist = now; persist = Job.Snapshot(); }
            if (now - _lastNotify >= NotifyInterval) { _lastNotify = now; notify = Job.Snapshot(); }
        }
        if (persist is not null) await _repository.UpdateAsync(persist, ct).ConfigureAwait(false);
        if (notify is not null) _notify(notify);
    }

    /// <summary>Enregistre (de façon persistante) le résultat d'un élément.</summary>
    public async Task CompleteItemAsync(string key, JobItemStatus status, string? message = null, CancellationToken ct = default)
    {
        JobItem item;
        Job? persist = null;
        Job? notify = null;
        lock (_gate)
        {
            if (!_items.TryGetValue(key, out item!))
            {
                item = new JobItem { JobId = Job.Id, ItemKey = key };
                _items[key] = item;
            }
            item.Status = status;
            item.Attempts++;
            item.Error = message;
            item.UpdatedAt = DateTimeOffset.UtcNow;
            if (status == JobItemStatus.Failed) Job.Failed++;
            Job.Processed = _items.Values.Count(i => i.Status is JobItemStatus.Completed or JobItemStatus.Skipped) + Job.Failed;
            var now = DateTimeOffset.UtcNow;
            if (now - _lastPersist >= PersistInterval) { _lastPersist = now; persist = Job.Snapshot(); }
            if (now - _lastNotify >= NotifyInterval) { _lastNotify = now; notify = Job.Snapshot(); }
            item = new JobItem { JobId = item.JobId, ItemKey = item.ItemKey, Status = item.Status, Attempts = item.Attempts, Error = item.Error, UpdatedAt = item.UpdatedAt };
        }
        await _repository.UpsertItemAsync(item, ct).ConfigureAwait(false);
        if (persist is not null) await _repository.UpdateAsync(persist, ct).ConfigureAwait(false);
        if (notify is not null) _notify(notify);
    }
}

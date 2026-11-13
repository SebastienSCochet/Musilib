using Musilib.Domain.Enums;

namespace Musilib.Domain.Entities;

/// <summary>Traitement persistant exécuté par le moteur de file d'attente.</summary>
public sealed class Job
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public JobType Type { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public Guid? PlaylistId { get; set; }
    public string Title { get; set; } = string.Empty;
    public int Total { get; set; }
    public int Processed { get; set; }
    public int Failed { get; set; }
    /// <summary>Paramètres du traitement (JSON).</summary>
    public string? Payload { get; set; }
    /// <summary>État intermédiaire permettant la reprise (JSON).</summary>
    public string? State { get; set; }
    public string? Message { get; set; }
    public string? CurrentItem { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public double Progress => Total <= 0 ? (Status is JobStatus.Completed ? 1 : 0) : Math.Clamp((double)Processed / Total, 0, 1);
    public bool IsTerminal => Status is JobStatus.Completed or JobStatus.PartiallyCompleted or JobStatus.Failed or JobStatus.Cancelled;
    public bool CanResume => Status is JobStatus.Paused or JobStatus.Failed or JobStatus.Cancelled or JobStatus.PartiallyCompleted;

    public Job Snapshot() => (Job)MemberwiseClone();
}

public sealed class JobItem
{
    public Guid JobId { get; set; }
    public string ItemKey { get; set; } = string.Empty;
    public JobItemStatus Status { get; set; }
    public int Attempts { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

using Musilib.Domain.Enums;

namespace Musilib.Domain.Entities;

public sealed class HistoryEntry
{
    public long Id { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public HistoryKind Kind { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Details { get; set; }
    public bool Success { get; set; } = true;
    public Guid? PlaylistId { get; set; }
}

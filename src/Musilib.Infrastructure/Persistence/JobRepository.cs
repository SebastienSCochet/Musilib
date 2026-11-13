using Microsoft.Data.Sqlite;
using Musilib.Application.Abstractions;
using Musilib.Domain.Entities;
using Musilib.Domain.Enums;

namespace Musilib.Infrastructure.Persistence;

public sealed class JobRepository : IJobRepository
{
    private readonly SqliteConnectionFactory _factory;

    public JobRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task InsertAsync(Job job, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.Command("""
            INSERT INTO Jobs (Id, Type, Status, PlaylistId, Title, Total, Processed, Failed, Payload, State, Message, CurrentItem, CreatedAt, StartedAt, CompletedAt)
            VALUES ($id, $type, $status, $pid, $title, $total, $processed, $failed, $payload, $state, $message, $current, $created, $started, $completed);
            """);
        Bind(cmd, job);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateAsync(Job job, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.Command("""
            UPDATE Jobs SET Type = $type, Status = $status, PlaylistId = $pid, Title = $title, Total = $total, Processed = $processed,
                Failed = $failed, Payload = $payload, State = $state, Message = $message, CurrentItem = $current,
                CreatedAt = $created, StartedAt = $started, CompletedAt = $completed
            WHERE Id = $id;
            """);
        Bind(cmd, job);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<Job?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var list = await QueryAsync("SELECT * FROM Jobs WHERE Id = $id;", c => c.With("$id", id), ct).ConfigureAwait(false);
        return list.FirstOrDefault();
    }

    public Task<IReadOnlyList<Job>> GetRecentAsync(int limit, CancellationToken ct = default) =>
        QueryAsync("SELECT * FROM Jobs ORDER BY CreatedAt DESC LIMIT $limit;", c => c.With("$limit", limit), ct);

    public async Task<IReadOnlyList<Job>> GetByStatusAsync(IReadOnlyCollection<JobStatus> statuses, CancellationToken ct = default)
    {
        if (statuses.Count == 0) return Array.Empty<Job>();
        var names = statuses.Select((s, i) => (Param: $"$s{i}", Value: s)).ToList();
        var sql = $"SELECT * FROM Jobs WHERE Status IN ({string.Join(", ", names.Select(n => n.Param))}) ORDER BY CreatedAt;";
        return await QueryAsync(sql, c =>
        {
            foreach (var (param, value) in names) c.With(param, value);
        }, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.Command("DELETE FROM Jobs WHERE Id = $id;").With("$id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> DeleteTerminatedAsync(CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.Command("DELETE FROM Jobs WHERE Status IN ('Completed', 'PartiallyCompleted', 'Failed', 'Cancelled');");
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task UpsertItemAsync(JobItem item, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.Command("""
            INSERT INTO JobItems (JobId, ItemKey, Status, Attempts, Error, UpdatedAt) VALUES ($j, $k, $s, $a, $e, $u)
            ON CONFLICT(JobId, ItemKey) DO UPDATE SET Status = excluded.Status, Attempts = excluded.Attempts, Error = excluded.Error, UpdatedAt = excluded.UpdatedAt;
            """).With("$j", item.JobId).With("$k", item.ItemKey).With("$s", item.Status).With("$a", item.Attempts)
            .With("$e", item.Error).With("$u", item.UpdatedAt);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<JobItem>> GetItemsAsync(Guid jobId, CancellationToken ct = default)
    {
        var list = new List<JobItem>();
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.Command("SELECT * FROM JobItems WHERE JobId = $j;").With("$j", jobId);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new JobItem
            {
                JobId = r.Guid("JobId"),
                ItemKey = r.StrOrEmpty("ItemKey"),
                Status = r.Enum("Status", JobItemStatus.Pending),
                Attempts = r.Int("Attempts"),
                Error = r.Str("Error"),
                UpdatedAt = r.Date("UpdatedAt") ?? DateTimeOffset.UtcNow
            });
        }
        return list;
    }

    private async Task<IReadOnlyList<Job>> QueryAsync(string sql, Action<SqliteCommand> bind, CancellationToken ct)
    {
        var list = new List<Job>();
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.Command(sql);
        bind(cmd);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new Job
            {
                Id = r.Guid("Id"),
                Type = r.Enum("Type", JobType.Import),
                Status = r.Enum("Status", JobStatus.Queued),
                PlaylistId = r.GuidOrNull("PlaylistId"),
                Title = r.StrOrEmpty("Title"),
                Total = r.Int("Total"),
                Processed = r.Int("Processed"),
                Failed = r.Int("Failed"),
                Payload = r.Str("Payload"),
                State = r.Str("State"),
                Message = r.Str("Message"),
                CurrentItem = r.Str("CurrentItem"),
                CreatedAt = r.Date("CreatedAt") ?? DateTimeOffset.UtcNow,
                StartedAt = r.Date("StartedAt"),
                CompletedAt = r.Date("CompletedAt")
            });
        }
        return list;
    }

    private static void Bind(SqliteCommand cmd, Job job) =>
        cmd.With("$id", job.Id).With("$type", job.Type).With("$status", job.Status).With("$pid", job.PlaylistId)
            .With("$title", job.Title).With("$total", job.Total).With("$processed", job.Processed).With("$failed", job.Failed)
            .With("$payload", job.Payload).With("$state", job.State).With("$message", job.Message).With("$current", job.CurrentItem)
            .With("$created", job.CreatedAt).With("$started", job.StartedAt).With("$completed", job.CompletedAt);
}

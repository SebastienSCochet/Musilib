using System.Globalization;
using Musilib.Application.Abstractions;
using Musilib.Application.Common;
using Musilib.Domain.Entities;
using Musilib.Domain.Enums;

namespace Musilib.Infrastructure.Persistence;

public sealed class HistoryRepository : IHistoryRepository
{
    private readonly SqliteConnectionFactory _factory;

    public HistoryRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task AddAsync(HistoryEntry entry, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.Command("""
            INSERT INTO History (Timestamp, Kind, Title, Details, Success, PlaylistId) VALUES ($t, $k, $title, $d, $s, $p);
            SELECT last_insert_rowid();
            """).With("$t", entry.Timestamp).With("$k", entry.Kind).With("$title", entry.Title).With("$d", entry.Details)
            .With("$s", entry.Success).With("$p", entry.PlaylistId);
        entry.Id = (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
    }

    public async Task<IReadOnlyList<HistoryEntry>> GetRecentAsync(int limit, HistoryKind? kind = null, CancellationToken ct = default)
    {
        var list = new List<HistoryEntry>();
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.Command("SELECT * FROM History WHERE ($k IS NULL OR Kind = $k) ORDER BY Id DESC LIMIT $l;")
            .With("$k", kind).With("$l", limit);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new HistoryEntry
            {
                Id = r.Long("Id") ?? 0,
                Timestamp = r.Date("Timestamp") ?? DateTimeOffset.UtcNow,
                Kind = r.Enum("Kind", HistoryKind.System),
                Title = r.StrOrEmpty("Title"),
                Details = r.Str("Details"),
                Success = r.Bool("Success"),
                PlaylistId = r.GuidOrNull("PlaylistId")
            });
        }
        return list;
    }

    public async Task<int> CountAsync(HistoryKind kind, bool successOnly, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.Command("SELECT COUNT(*) FROM History WHERE Kind = $k AND ($s = 0 OR Success = 1);")
            .With("$k", kind).With("$s", successOnly);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.Command("DELETE FROM History;");
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}

public sealed class SearchCacheRepository : ISearchCache
{
    private readonly SqliteConnectionFactory _factory;
    private readonly TimeProvider _time;

    public SearchCacheRepository(SqliteConnectionFactory factory, TimeProvider time)
    {
        _factory = factory;
        _time = time;
    }

    public async Task<IReadOnlyList<SpotifyCandidate>?> TryGetAsync(string key, TimeSpan maxAge, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.Command("SELECT ResultJson, CreatedAt FROM SearchCache WHERE QueryKey = $k;").With("$k", key);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) return null;
        var created = r.Date("CreatedAt");
        if (created is null || _time.GetUtcNow() - created.Value > maxAge) return null;
        return JsonDefaults.Deserialize<List<SpotifyCandidate>>(r.Str("ResultJson"));
    }

    public async Task SetAsync(string key, IReadOnlyList<SpotifyCandidate> results, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.Command("INSERT OR REPLACE INTO SearchCache (QueryKey, ResultJson, CreatedAt) VALUES ($k, $j, $c);")
            .With("$k", key).With("$j", JsonDefaults.Serialize(results)).With("$c", _time.GetUtcNow());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.Command("SELECT COUNT(*) FROM SearchCache;");
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.Command("DELETE FROM SearchCache;");
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}

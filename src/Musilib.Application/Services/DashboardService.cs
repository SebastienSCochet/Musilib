using Musilib.Application.Abstractions;
using Musilib.Domain.Entities;
using Musilib.Domain.Enums;

namespace Musilib.Application.Services;

public sealed record DashboardData(
    int PlaylistCount,
    PlaylistStats Totals,
    int TransfersDone,
    int ExportsDone,
    int CachedSearches,
    IReadOnlyList<Playlist> RecentPlaylists,
    IReadOnlyList<Job> RecentJobs,
    IReadOnlyList<HistoryEntry> RecentHistory);

public sealed class DashboardService
{
    private readonly IPlaylistRepository _playlists;
    private readonly ITrackRepository _tracks;
    private readonly IJobRepository _jobs;
    private readonly IHistoryRepository _history;
    private readonly ISearchCache _cache;

    public DashboardService(IPlaylistRepository playlists, ITrackRepository tracks, IJobRepository jobs, IHistoryRepository history, ISearchCache cache)
    {
        _playlists = playlists;
        _tracks = tracks;
        _jobs = jobs;
        _history = history;
        _cache = cache;
    }

    public async Task<DashboardData> GetAsync(CancellationToken ct)
    {
        var playlists = await _playlists.GetAllAsync(ct).ConfigureAwait(false);
        var totals = await _tracks.GetStatsAsync(null, ct).ConfigureAwait(false);
        var transfers = await _history.CountAsync(HistoryKind.Transfer, true, ct).ConfigureAwait(false);
        var exports = await _history.CountAsync(HistoryKind.Export, true, ct).ConfigureAwait(false);
        var cache = await _cache.CountAsync(ct).ConfigureAwait(false);
        var jobs = await _jobs.GetRecentAsync(6, ct).ConfigureAwait(false);
        var history = await _history.GetRecentAsync(8, null, ct).ConfigureAwait(false);
        var recent = playlists.OrderByDescending(p => p.LastSyncedAt ?? p.ImportedAt).Take(4).ToList();
        return new DashboardData(playlists.Count, totals, transfers, exports, cache, recent, jobs, history);
    }
}

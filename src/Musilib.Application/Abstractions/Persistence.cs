using Musilib.Domain.Entities;
using Musilib.Domain.Enums;

namespace Musilib.Application.Abstractions;

public interface IPlaylistRepository
{
    Task<IReadOnlyList<Playlist>> GetAllAsync(CancellationToken ct = default);
    Task<Playlist?> GetAsync(Guid id, CancellationToken ct = default);
    Task<Playlist?> FindByExternalIdAsync(PlaylistSource source, string externalId, CancellationToken ct = default);
    Task UpsertAsync(Playlist playlist, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

public interface ITrackRepository
{
    Task<IReadOnlyList<Track>> GetByPlaylistAsync(Guid playlistId, CancellationToken ct = default);
    Task<Track?> GetAsync(Guid id, CancellationToken ct = default);
    Task UpsertManyAsync(IReadOnlyCollection<Track> tracks, CancellationToken ct = default);
    Task UpdateAsync(Track track, CancellationToken ct = default);
    Task SaveCandidatesAsync(Guid trackId, IReadOnlyList<SpotifyCandidate> candidates, CancellationToken ct = default);
    Task<IReadOnlyList<SpotifyCandidate>> GetCandidatesAsync(Guid trackId, CancellationToken ct = default);
    Task<PlaylistStats> GetStatsAsync(Guid? playlistId, CancellationToken ct = default);
}

public interface IJobRepository
{
    Task InsertAsync(Job job, CancellationToken ct = default);
    Task UpdateAsync(Job job, CancellationToken ct = default);
    Task<Job?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Job>> GetRecentAsync(int limit, CancellationToken ct = default);
    Task<IReadOnlyList<Job>> GetByStatusAsync(IReadOnlyCollection<JobStatus> statuses, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task<int> DeleteTerminatedAsync(CancellationToken ct = default);
    Task UpsertItemAsync(JobItem item, CancellationToken ct = default);
    Task<IReadOnlyList<JobItem>> GetItemsAsync(Guid jobId, CancellationToken ct = default);
}

public interface IHistoryRepository
{
    Task AddAsync(HistoryEntry entry, CancellationToken ct = default);
    Task<IReadOnlyList<HistoryEntry>> GetRecentAsync(int limit, HistoryKind? kind = null, CancellationToken ct = default);
    Task<int> CountAsync(HistoryKind kind, bool successOnly, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}

/// <summary>Cache persistant des recherches Spotify (évite les appels API inutiles).</summary>
public interface ISearchCache
{
    Task<IReadOnlyList<SpotifyCandidate>?> TryGetAsync(string key, TimeSpan maxAge, CancellationToken ct = default);
    Task SetAsync(string key, IReadOnlyList<SpotifyCandidate> results, CancellationToken ct = default);
    Task<int> CountAsync(CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}

/// <summary>Initialisation et migration du schéma de base de données.</summary>
public interface IDatabaseInitializer
{
    Task InitializeAsync(CancellationToken ct = default);
    string DatabasePath { get; }
}

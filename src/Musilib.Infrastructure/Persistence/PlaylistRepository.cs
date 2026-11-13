using Musilib.Application.Abstractions;
using Musilib.Domain.Entities;
using Musilib.Domain.Enums;

namespace Musilib.Infrastructure.Persistence;

public sealed class PlaylistRepository : IPlaylistRepository
{
    private readonly SqliteConnectionFactory _factory;
    private readonly ITrackRepository _tracks;

    public PlaylistRepository(SqliteConnectionFactory factory, ITrackRepository tracks)
    {
        _factory = factory;
        _tracks = tracks;
    }

    public async Task<IReadOnlyList<Playlist>> GetAllAsync(CancellationToken ct = default)
    {
        var list = new List<Playlist>();
        await using (var connection = await _factory.OpenAsync(ct).ConfigureAwait(false))
        await using (var cmd = connection.Command("SELECT * FROM Playlists ORDER BY COALESCE(LastSyncedAt, ImportedAt) DESC;"))
        await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await r.ReadAsync(ct).ConfigureAwait(false)) list.Add(Map(r));
        }
        foreach (var p in list) p.Stats = await _tracks.GetStatsAsync(p.Id, ct).ConfigureAwait(false);
        return list;
    }

    public async Task<Playlist?> GetAsync(Guid id, CancellationToken ct = default)
    {
        Playlist? playlist = null;
        await using (var connection = await _factory.OpenAsync(ct).ConfigureAwait(false))
        await using (var cmd = connection.Command("SELECT * FROM Playlists WHERE Id = $id;").With("$id", id))
        await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (await r.ReadAsync(ct).ConfigureAwait(false)) playlist = Map(r);
        }
        if (playlist is not null) playlist.Stats = await _tracks.GetStatsAsync(playlist.Id, ct).ConfigureAwait(false);
        return playlist;
    }

    public async Task<Playlist?> FindByExternalIdAsync(PlaylistSource source, string externalId, CancellationToken ct = default)
    {
        Guid? id = null;
        await using (var connection = await _factory.OpenAsync(ct).ConfigureAwait(false))
        await using (var cmd = connection.Command("SELECT Id FROM Playlists WHERE Source = $s AND ExternalId = $e;").With("$s", source).With("$e", externalId))
        {
            var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (result is string s) id = Guid.Parse(s);
        }
        return id is null ? null : await GetAsync(id.Value, ct).ConfigureAwait(false);
    }

    public async Task UpsertAsync(Playlist p, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.Command("""
            INSERT INTO Playlists (Id, Source, ExternalId, Title, Description, ThumbnailUrl, Owner, SourceItemCount, ImportedAt, LastSyncedAt)
            VALUES ($id, $source, $ext, $title, $desc, $thumb, $owner, $count, $imported, $synced)
            ON CONFLICT(Id) DO UPDATE SET
                Title = excluded.Title, Description = excluded.Description, ThumbnailUrl = excluded.ThumbnailUrl,
                Owner = excluded.Owner, SourceItemCount = excluded.SourceItemCount, ImportedAt = excluded.ImportedAt,
                LastSyncedAt = excluded.LastSyncedAt;
            """)
            .With("$id", p.Id).With("$source", p.Source).With("$ext", p.ExternalId).With("$title", p.Title)
            .With("$desc", p.Description).With("$thumb", p.ThumbnailUrl).With("$owner", p.Owner)
            .With("$count", p.SourceItemCount).With("$imported", p.ImportedAt).With("$synced", p.LastSyncedAt);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.Command("DELETE FROM Playlists WHERE Id = $id;").With("$id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static Playlist Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = r.Guid("Id"),
        Source = r.Enum("Source", PlaylistSource.YouTube),
        ExternalId = r.StrOrEmpty("ExternalId"),
        Title = r.StrOrEmpty("Title"),
        Description = r.Str("Description"),
        ThumbnailUrl = r.Str("ThumbnailUrl"),
        Owner = r.Str("Owner"),
        SourceItemCount = r.Int("SourceItemCount"),
        ImportedAt = r.Date("ImportedAt"),
        LastSyncedAt = r.Date("LastSyncedAt")
    };
}

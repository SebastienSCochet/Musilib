using Microsoft.Data.Sqlite;
using Musilib.Application.Abstractions;
using Musilib.Application.Common;
using Musilib.Domain.Entities;
using Musilib.Domain.Enums;

namespace Musilib.Infrastructure.Persistence;

public sealed class TrackRepository : ITrackRepository
{
    private const string UpsertSql = """
        INSERT INTO Tracks (Id, PlaylistId, Position, SourceItemId, SourceVideoId, SourceTitle, SourceChannel, SourceDurationMs,
            SourceThumbnailUrl, Availability, RemovedFromSource, Title, Artists, Album, AlbumArtists, ReleaseDate, DurationMs, Isrc,
            CoverUrl, CoverLocalPath, Genre, TrackNumber, DiscNumber, Comment, MetadataOrigin, MetadataEdited, MatchStatus, MatchScore,
            SpotifyTrackId, SpotifyUri, SpotifyUrl, PreviewUrl, CustomSearchQuery, LocalFilePath, Excluded, LastError, UpdatedAt)
        VALUES ($Id, $PlaylistId, $Position, $SourceItemId, $SourceVideoId, $SourceTitle, $SourceChannel, $SourceDurationMs,
            $SourceThumbnailUrl, $Availability, $RemovedFromSource, $Title, $Artists, $Album, $AlbumArtists, $ReleaseDate, $DurationMs, $Isrc,
            $CoverUrl, $CoverLocalPath, $Genre, $TrackNumber, $DiscNumber, $Comment, $MetadataOrigin, $MetadataEdited, $MatchStatus, $MatchScore,
            $SpotifyTrackId, $SpotifyUri, $SpotifyUrl, $PreviewUrl, $CustomSearchQuery, $LocalFilePath, $Excluded, $LastError, $UpdatedAt)
        ON CONFLICT(Id) DO UPDATE SET
            Position = excluded.Position, SourceItemId = excluded.SourceItemId, SourceVideoId = excluded.SourceVideoId,
            SourceTitle = excluded.SourceTitle, SourceChannel = excluded.SourceChannel, SourceDurationMs = excluded.SourceDurationMs,
            SourceThumbnailUrl = excluded.SourceThumbnailUrl, Availability = excluded.Availability, RemovedFromSource = excluded.RemovedFromSource,
            Title = excluded.Title, Artists = excluded.Artists, Album = excluded.Album, AlbumArtists = excluded.AlbumArtists,
            ReleaseDate = excluded.ReleaseDate, DurationMs = excluded.DurationMs, Isrc = excluded.Isrc, CoverUrl = excluded.CoverUrl,
            CoverLocalPath = excluded.CoverLocalPath, Genre = excluded.Genre, TrackNumber = excluded.TrackNumber, DiscNumber = excluded.DiscNumber,
            Comment = excluded.Comment, MetadataOrigin = excluded.MetadataOrigin, MetadataEdited = excluded.MetadataEdited,
            MatchStatus = excluded.MatchStatus, MatchScore = excluded.MatchScore, SpotifyTrackId = excluded.SpotifyTrackId,
            SpotifyUri = excluded.SpotifyUri, SpotifyUrl = excluded.SpotifyUrl, PreviewUrl = excluded.PreviewUrl,
            CustomSearchQuery = excluded.CustomSearchQuery, LocalFilePath = excluded.LocalFilePath, Excluded = excluded.Excluded,
            LastError = excluded.LastError, UpdatedAt = excluded.UpdatedAt;
        """;

    private readonly SqliteConnectionFactory _factory;

    public TrackRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<Track>> GetByPlaylistAsync(Guid playlistId, CancellationToken ct = default)
    {
        var list = new List<Track>();
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.Command("SELECT * FROM Tracks WHERE PlaylistId = $p ORDER BY Position;").With("$p", playlistId);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false)) list.Add(Map(r));
        return list;
    }

    public async Task<Track?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.Command("SELECT * FROM Tracks WHERE Id = $id;").With("$id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? Map(r) : null;
    }

    public async Task UpsertManyAsync(IReadOnlyCollection<Track> tracks, CancellationToken ct = default)
    {
        if (tracks.Count == 0) return;
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (var t in tracks)
        {
            await using var cmd = connection.Command(UpsertSql, tx);
            Bind(cmd, t);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public Task UpdateAsync(Track track, CancellationToken ct = default) => UpsertManyAsync(new[] { track }, ct);

    public async Task SaveCandidatesAsync(Guid trackId, IReadOnlyList<SpotifyCandidate> candidates, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (var del = connection.Command("DELETE FROM TrackCandidates WHERE TrackId = $t;", tx).With("$t", trackId))
        {
            await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        for (var i = 0; i < candidates.Count; i++)
        {
            await using var ins = connection.Command("INSERT INTO TrackCandidates (TrackId, Rank, Score, Data) VALUES ($t, $r, $s, $d);", tx)
                .With("$t", trackId).With("$r", i).With("$s", candidates[i].Score).With("$d", JsonDefaults.Serialize(candidates[i]));
            await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SpotifyCandidate>> GetCandidatesAsync(Guid trackId, CancellationToken ct = default)
    {
        var list = new List<SpotifyCandidate>();
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.Command("SELECT Data FROM TrackCandidates WHERE TrackId = $t ORDER BY Rank;").With("$t", trackId);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var c = JsonDefaults.Deserialize<SpotifyCandidate>(r.GetString(0));
            if (c is not null) list.Add(c);
        }
        return list;
    }

    public async Task<PlaylistStats> GetStatsAsync(Guid? playlistId, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.Command("""
            SELECT COUNT(*) AS Total,
                TOTAL(CASE WHEN Availability <> 'Available' OR RemovedFromSource = 1 THEN 1 ELSE 0 END) AS Unavailable,
                TOTAL(CASE WHEN MatchStatus = 'Pending' THEN 1 ELSE 0 END) AS Pending,
                TOTAL(CASE WHEN MatchStatus = 'AutoMatched' THEN 1 ELSE 0 END) AS AutoMatched,
                TOTAL(CASE WHEN MatchStatus = 'Ambiguous' THEN 1 ELSE 0 END) AS Ambiguous,
                TOTAL(CASE WHEN MatchStatus = 'NoMatch' THEN 1 ELSE 0 END) AS NoMatch,
                TOTAL(CASE WHEN MatchStatus = 'Confirmed' THEN 1 ELSE 0 END) AS Confirmed,
                TOTAL(CASE WHEN MatchStatus = 'Error' THEN 1 ELSE 0 END) AS Errors,
                TOTAL(CASE WHEN Excluded = 1 THEN 1 ELSE 0 END) AS Excluded,
                TOTAL(CASE WHEN LocalFilePath IS NOT NULL AND LocalFilePath <> '' THEN 1 ELSE 0 END) AS WithLocalFile
            FROM Tracks WHERE ($p IS NULL OR PlaylistId = $p);
            """).With("$p", playlistId);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) return new PlaylistStats();
        return new PlaylistStats
        {
            Total = r.Int("Total"),
            Unavailable = (int)(r.Double("Unavailable") ?? 0),
            Pending = (int)(r.Double("Pending") ?? 0),
            AutoMatched = (int)(r.Double("AutoMatched") ?? 0),
            Ambiguous = (int)(r.Double("Ambiguous") ?? 0),
            NoMatch = (int)(r.Double("NoMatch") ?? 0),
            Confirmed = (int)(r.Double("Confirmed") ?? 0),
            Errors = (int)(r.Double("Errors") ?? 0),
            Excluded = (int)(r.Double("Excluded") ?? 0),
            WithLocalFile = (int)(r.Double("WithLocalFile") ?? 0)
        };
    }

    private static void Bind(SqliteCommand cmd, Track t)
    {
        var m = t.Metadata;
        cmd.With("$Id", t.Id).With("$PlaylistId", t.PlaylistId).With("$Position", t.Position)
            .With("$SourceItemId", t.SourceItemId).With("$SourceVideoId", t.SourceVideoId).With("$SourceTitle", t.SourceTitle)
            .With("$SourceChannel", t.SourceChannel).With("$SourceDurationMs", t.SourceDurationMs).With("$SourceThumbnailUrl", t.SourceThumbnailUrl)
            .With("$Availability", t.Availability).With("$RemovedFromSource", t.RemovedFromSource)
            .With("$Title", m.Title).With("$Artists", JsonDefaults.Serialize(m.Artists)).With("$Album", m.Album)
            .With("$AlbumArtists", JsonDefaults.Serialize(m.AlbumArtists)).With("$ReleaseDate", m.ReleaseDate).With("$DurationMs", m.DurationMs)
            .With("$Isrc", m.Isrc).With("$CoverUrl", m.CoverUrl).With("$CoverLocalPath", m.CoverLocalPath).With("$Genre", m.Genre)
            .With("$TrackNumber", m.TrackNumber).With("$DiscNumber", m.DiscNumber).With("$Comment", m.Comment)
            .With("$MetadataOrigin", t.MetadataOrigin).With("$MetadataEdited", t.MetadataEdited)
            .With("$MatchStatus", t.MatchStatus).With("$MatchScore", t.MatchScore).With("$SpotifyTrackId", t.SpotifyTrackId)
            .With("$SpotifyUri", t.SpotifyUri).With("$SpotifyUrl", t.SpotifyUrl).With("$PreviewUrl", t.PreviewUrl)
            .With("$CustomSearchQuery", t.CustomSearchQuery).With("$LocalFilePath", t.LocalFilePath).With("$Excluded", t.Excluded)
            .With("$LastError", t.LastError).With("$UpdatedAt", t.UpdatedAt);
    }

    private static Track Map(SqliteDataReader r) => new()
    {
        Id = r.Guid("Id"),
        PlaylistId = r.Guid("PlaylistId"),
        Position = r.Int("Position"),
        SourceItemId = r.StrOrEmpty("SourceItemId"),
        SourceVideoId = r.StrOrEmpty("SourceVideoId"),
        SourceTitle = r.StrOrEmpty("SourceTitle"),
        SourceChannel = r.Str("SourceChannel"),
        SourceDurationMs = r.Long("SourceDurationMs"),
        SourceThumbnailUrl = r.Str("SourceThumbnailUrl"),
        Availability = r.Enum("Availability", SourceAvailability.Available),
        RemovedFromSource = r.Bool("RemovedFromSource"),
        Metadata = new TrackMetadata
        {
            Title = r.StrOrEmpty("Title"),
            Artists = JsonDefaults.Deserialize<List<string>>(r.Str("Artists")) ?? new(),
            Album = r.Str("Album"),
            AlbumArtists = JsonDefaults.Deserialize<List<string>>(r.Str("AlbumArtists")) ?? new(),
            ReleaseDate = r.Str("ReleaseDate"),
            DurationMs = r.Long("DurationMs"),
            Isrc = r.Str("Isrc"),
            CoverUrl = r.Str("CoverUrl"),
            CoverLocalPath = r.Str("CoverLocalPath"),
            Genre = r.Str("Genre"),
            TrackNumber = r.IntOrNull("TrackNumber"),
            DiscNumber = r.IntOrNull("DiscNumber"),
            Comment = r.Str("Comment")
        },
        MetadataOrigin = r.Enum("MetadataOrigin", MetadataOrigin.Source),
        MetadataEdited = r.Bool("MetadataEdited"),
        MatchStatus = r.Enum("MatchStatus", MatchStatus.Pending),
        MatchScore = r.Double("MatchScore"),
        SpotifyTrackId = r.Str("SpotifyTrackId"),
        SpotifyUri = r.Str("SpotifyUri"),
        SpotifyUrl = r.Str("SpotifyUrl"),
        PreviewUrl = r.Str("PreviewUrl"),
        CustomSearchQuery = r.Str("CustomSearchQuery"),
        LocalFilePath = r.Str("LocalFilePath"),
        Excluded = r.Bool("Excluded"),
        LastError = r.Str("LastError"),
        UpdatedAt = r.Date("UpdatedAt") ?? DateTimeOffset.UtcNow
    };
}

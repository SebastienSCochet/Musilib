using Microsoft.Extensions.Logging;
using Musilib.Application.Abstractions;

namespace Musilib.Infrastructure.Persistence;

/// <summary>Crée la base SQLite et applique les migrations de schéma de manière idempotente.</summary>
public sealed class DatabaseInitializer : IDatabaseInitializer
{
    private static readonly string[] Migrations =
    [
        // Version 1 : schéma initial
        """
        CREATE TABLE IF NOT EXISTS Playlists (
            Id TEXT PRIMARY KEY,
            Source TEXT NOT NULL,
            ExternalId TEXT NOT NULL,
            Title TEXT NOT NULL,
            Description TEXT,
            ThumbnailUrl TEXT,
            Owner TEXT,
            SourceItemCount INTEGER NOT NULL DEFAULT 0,
            ImportedAt TEXT,
            LastSyncedAt TEXT,
            UNIQUE (Source, ExternalId)
        );

        CREATE TABLE IF NOT EXISTS Tracks (
            Id TEXT PRIMARY KEY,
            PlaylistId TEXT NOT NULL REFERENCES Playlists(Id) ON DELETE CASCADE,
            Position INTEGER NOT NULL,
            SourceItemId TEXT NOT NULL,
            SourceVideoId TEXT NOT NULL,
            SourceTitle TEXT NOT NULL,
            SourceChannel TEXT,
            SourceDurationMs INTEGER,
            SourceThumbnailUrl TEXT,
            Availability TEXT NOT NULL,
            RemovedFromSource INTEGER NOT NULL DEFAULT 0,
            Title TEXT NOT NULL,
            Artists TEXT NOT NULL,
            Album TEXT,
            AlbumArtists TEXT,
            ReleaseDate TEXT,
            DurationMs INTEGER,
            Isrc TEXT,
            CoverUrl TEXT,
            CoverLocalPath TEXT,
            Genre TEXT,
            TrackNumber INTEGER,
            DiscNumber INTEGER,
            Comment TEXT,
            MetadataOrigin TEXT NOT NULL,
            MetadataEdited INTEGER NOT NULL DEFAULT 0,
            MatchStatus TEXT NOT NULL,
            MatchScore REAL,
            SpotifyTrackId TEXT,
            SpotifyUri TEXT,
            SpotifyUrl TEXT,
            PreviewUrl TEXT,
            CustomSearchQuery TEXT,
            LocalFilePath TEXT,
            Excluded INTEGER NOT NULL DEFAULT 0,
            LastError TEXT,
            UpdatedAt TEXT NOT NULL,
            UNIQUE (PlaylistId, SourceItemId)
        );
        CREATE INDEX IF NOT EXISTS IX_Tracks_Playlist_Position ON Tracks (PlaylistId, Position);

        CREATE TABLE IF NOT EXISTS TrackCandidates (
            TrackId TEXT NOT NULL REFERENCES Tracks(Id) ON DELETE CASCADE,
            Rank INTEGER NOT NULL,
            Score REAL NOT NULL,
            Data TEXT NOT NULL,
            PRIMARY KEY (TrackId, Rank)
        );

        CREATE TABLE IF NOT EXISTS SearchCache (
            QueryKey TEXT PRIMARY KEY,
            ResultJson TEXT NOT NULL,
            CreatedAt TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS Jobs (
            Id TEXT PRIMARY KEY,
            Type TEXT NOT NULL,
            Status TEXT NOT NULL,
            PlaylistId TEXT,
            Title TEXT NOT NULL,
            Total INTEGER NOT NULL DEFAULT 0,
            Processed INTEGER NOT NULL DEFAULT 0,
            Failed INTEGER NOT NULL DEFAULT 0,
            Payload TEXT,
            State TEXT,
            Message TEXT,
            CurrentItem TEXT,
            CreatedAt TEXT NOT NULL,
            StartedAt TEXT,
            CompletedAt TEXT
        );
        CREATE INDEX IF NOT EXISTS IX_Jobs_Status ON Jobs (Status);

        CREATE TABLE IF NOT EXISTS JobItems (
            JobId TEXT NOT NULL REFERENCES Jobs(Id) ON DELETE CASCADE,
            ItemKey TEXT NOT NULL,
            Status TEXT NOT NULL,
            Attempts INTEGER NOT NULL DEFAULT 0,
            Error TEXT,
            UpdatedAt TEXT NOT NULL,
            PRIMARY KEY (JobId, ItemKey)
        );

        CREATE TABLE IF NOT EXISTS History (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Timestamp TEXT NOT NULL,
            Kind TEXT NOT NULL,
            Title TEXT NOT NULL,
            Details TEXT,
            Success INTEGER NOT NULL DEFAULT 1,
            PlaylistId TEXT
        );
        CREATE INDEX IF NOT EXISTS IX_History_Timestamp ON History (Timestamp);
        """
    ];

    private readonly SqliteConnectionFactory _factory;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(SqliteConnectionFactory factory, ILogger<DatabaseInitializer> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public string DatabasePath => _factory.DatabasePath;

    public static int LatestVersion => Migrations.Length;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using (var wal = connection.Command("PRAGMA journal_mode = WAL;"))
        {
            await wal.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var create = connection.Command("CREATE TABLE IF NOT EXISTS SchemaVersion (Version INTEGER NOT NULL);"))
        {
            await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        long current;
        await using (var get = connection.Command("SELECT COALESCE(MAX(Version), 0) FROM SchemaVersion;"))
        {
            current = (long)(await get.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
        }

        for (var v = (int)current; v < Migrations.Length; v++)
        {
            await using var tx = (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using (var migrate = connection.Command(Migrations[v], tx))
            {
                await migrate.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await using (var record = connection.Command("INSERT INTO SchemaVersion (Version) VALUES ($v);", tx).With("$v", v + 1))
            {
                await record.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await tx.CommitAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Migration de schéma appliquée : version {Version}", v + 1);
        }
    }
}

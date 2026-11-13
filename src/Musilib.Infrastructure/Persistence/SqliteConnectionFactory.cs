using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Musilib.Infrastructure.Persistence;

/// <summary>Fabrique de connexions SQLite (WAL, clés étrangères, écriture synchrone pour la résistance aux coupures).</summary>
public sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;

    public SqliteConnectionFactory(string databasePath)
    {
        DatabasePath = databasePath;
        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 30
        }.ToString();
    }

    public string DatabasePath { get; }

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA synchronous = FULL; PRAGMA busy_timeout = 30000;";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return connection;
    }
}

internal static class SqliteExtensions
{
    public static SqliteCommand Command(this SqliteConnection connection, string sql, SqliteTransaction? tx = null)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        return cmd;
    }

    public static SqliteCommand With(this SqliteCommand cmd, string name, object? value)
    {
        cmd.Parameters.AddWithValue(name, ToDb(value));
        return cmd;
    }

    public static object ToDb(object? value) => value switch
    {
        null => DBNull.Value,
        Guid g => g.ToString("D"),
        DateTimeOffset d => d.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        bool b => b ? 1 : 0,
        Enum e => e.ToString(),
        _ => value
    };

    public static string? Str(this SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    public static string StrOrEmpty(this SqliteDataReader r, string column) => r.Str(column) ?? string.Empty;

    public static Guid Guid(this SqliteDataReader r, string column) => System.Guid.Parse(r.GetString(r.GetOrdinal(column)));

    public static Guid? GuidOrNull(this SqliteDataReader r, string column)
    {
        var s = r.Str(column);
        return s is null ? null : System.Guid.Parse(s);
    }

    public static long? Long(this SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetInt64(i);
    }

    public static int Int(this SqliteDataReader r, string column) => (int)(r.Long(column) ?? 0);

    public static int? IntOrNull(this SqliteDataReader r, string column) => r.Long(column) is { } v ? (int)v : null;

    public static double? Double(this SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetDouble(i);
    }

    public static bool Bool(this SqliteDataReader r, string column) => (r.Long(column) ?? 0) != 0;

    public static DateTimeOffset? Date(this SqliteDataReader r, string column)
    {
        var s = r.Str(column);
        return s is null ? null : DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    public static T Enum<T>(this SqliteDataReader r, string column, T fallback) where T : struct, Enum =>
        System.Enum.TryParse<T>(r.Str(column), out var v) ? v : fallback;
}

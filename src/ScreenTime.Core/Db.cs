using Microsoft.Data.Sqlite;

namespace ScreenTime.Core;

/// <summary>Opens the SQLite database and creates the schema on first use (DESIGN §3).</summary>
public static class Db
{
    public const int SchemaVersion = 1;

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS apps (
          id            INTEGER PRIMARY KEY,
          process_name  TEXT NOT NULL UNIQUE COLLATE NOCASE,
          display_name  TEXT NOT NULL,
          exe_path      TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS sessions (
          id         INTEGER PRIMARY KEY,
          app_id     INTEGER NOT NULL REFERENCES apps(id),
          start_utc  INTEGER NOT NULL,
          end_utc    INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_sessions_start ON sessions(start_utc);

        CREATE TABLE IF NOT EXISTS settings (
          key    TEXT PRIMARY KEY,
          value  TEXT NOT NULL
        );
        """;

    /// <summary>Opens (creating if needed) the database at <paramref name="path"/>. The caller owns the connection.</summary>
    public static SqliteConnection Open(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        // Pooling off: connections release the file as soon as they are closed,
        // so the dashboard never holds the database open between refreshes.
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            Execute(connection, "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;");
            EnsureSchema(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public static SqliteConnection Open(AppPaths paths) => Open(paths.DatabasePath);

    /// <summary>
    /// Opens an existing database read-only, for the dashboard and --dump-today. A read-only
    /// connection can never write or checkpoint, so readers can't disturb the tracker's writes.
    /// Throws <see cref="FileNotFoundException"/> if the tracker hasn't created the database yet.
    /// </summary>
    public static SqliteConnection OpenReadOnly(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("The screen time database doesn't exist yet.", path);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            Execute(connection, "PRAGMA busy_timeout=5000;");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public static SqliteConnection OpenReadOnly(AppPaths paths) => OpenReadOnly(paths.DatabasePath);

    private static void EnsureSchema(SqliteConnection connection)
    {
        if (GetUserVersion(connection) >= SchemaVersion)
            return;

        // Safe if the tracker and dashboard race on first run: every statement is IF NOT EXISTS.
        using var transaction = connection.BeginTransaction();
        Execute(connection, SchemaSql, transaction);
        Execute(connection, $"PRAGMA user_version = {SchemaVersion};", transaction);
        transaction.Commit();
    }

    internal static long GetUserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return (long)command.ExecuteScalar()!;
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

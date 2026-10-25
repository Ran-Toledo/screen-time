using Microsoft.Data.Sqlite;

namespace ScreenTime.Core;

/// <summary>Tracker-side writes: apps and sessions. The caller owns the connection.</summary>
/// <remarks>
/// App ids are deliberately not cached: they are looked up on every new session, so the
/// tracker can never attribute time to the wrong app if the database changes underneath it.
/// </remarks>
public sealed class SessionStore(SqliteConnection connection) : ISessionSink
{
    /// <summary>Returns the app's id, creating it or refreshing its display name and path (R7, R8).</summary>
    public long GetOrCreateApp(ForegroundApp app)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO apps (process_name, display_name, exe_path) VALUES ($name, $display, $path)
            ON CONFLICT (process_name) DO UPDATE SET
              display_name = excluded.display_name,
              exe_path = COALESCE(excluded.exe_path, apps.exe_path)
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$name", app.ProcessName);
        command.Parameters.AddWithValue("$display", app.DisplayName);
        command.Parameters.AddWithValue("$path", (object?)app.ExePath ?? DBNull.Value);
        return (long)command.ExecuteScalar()!;
    }

    public long Open(ForegroundApp app, long startUtc)
    {
        var appId = GetOrCreateApp(app);

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sessions (app_id, start_utc, end_utc) VALUES ($app, $start, $start)
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$app", appId);
        command.Parameters.AddWithValue("$start", startUtc);
        return (long)command.ExecuteScalar()!;
    }

    public bool UpdateEnd(long sessionId, long endUtc)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sessions SET end_utc = $end WHERE id = $id;";
        command.Parameters.AddWithValue("$end", endUtc);
        command.Parameters.AddWithValue("$id", sessionId);
        return command.ExecuteNonQuery() > 0;
    }
}

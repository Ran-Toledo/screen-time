using Microsoft.Data.Sqlite;

namespace ScreenTime.Core;

/// <summary>Tracker-side writes: apps and sessions. The caller owns the connection.</summary>
public sealed class SessionStore(SqliteConnection connection) : ISessionSink
{
    // Avoids rewriting the apps row on every session for an app we've already seen.
    private readonly Dictionary<string, (long Id, ForegroundApp App)> _apps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the app's id, creating it or refreshing its display name and path if they changed (R7, R8).</summary>
    public long GetOrCreateApp(ForegroundApp app)
    {
        if (_apps.TryGetValue(app.ProcessName, out var cached)
            && cached.App.DisplayName == app.DisplayName
            && (app.ExePath is null || cached.App.ExePath == app.ExePath))
        {
            return cached.Id;
        }

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

        var id = (long)command.ExecuteScalar()!;
        _apps[app.ProcessName] = (id, app);
        return id;
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

    public void UpdateEnd(long sessionId, long endUtc)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sessions SET end_utc = $end WHERE id = $id;";
        command.Parameters.AddWithValue("$end", endUtc);
        command.Parameters.AddWithValue("$id", sessionId);
        command.ExecuteNonQuery();
    }
}

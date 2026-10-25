using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ScreenTime.Core;

/// <summary>Key/value settings shared by the tracker and dashboard. The caller owns the connection.</summary>
public sealed class SettingsStore(SqliteConnection connection)
{
    public const string IdleThresholdKey = "idle_threshold_seconds";
    public const string StartupInitializedKey = "startup_initialized";

    public const int DefaultIdleThresholdSeconds = 300;
    public const int MinIdleThresholdSeconds = 60;
    public const int MaxIdleThresholdSeconds = 1800;

    public string? Get(string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public void Set(string key, string value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO settings (key, value) VALUES ($key, $value)
            ON CONFLICT (key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    /// <summary>The idle threshold (R4): 300 s when unset or unreadable, always clamped to 60–1800 s.</summary>
    public int GetIdleThresholdSeconds()
    {
        var stored = Get(IdleThresholdKey);
        return int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? ClampIdleThreshold(seconds)
            : DefaultIdleThresholdSeconds;
    }

    public void SetIdleThresholdSeconds(int seconds) =>
        Set(IdleThresholdKey, ClampIdleThreshold(seconds).ToString(CultureInfo.InvariantCulture));

    public static int ClampIdleThreshold(int seconds) =>
        Math.Clamp(seconds, MinIdleThresholdSeconds, MaxIdleThresholdSeconds);
}

using Microsoft.Data.Sqlite;
using ScreenTime.Core;

namespace ScreenTime.Tests;

/// <summary>A database in a fresh temp folder, deleted on dispose.</summary>
internal sealed class TestDb : IDisposable
{
    public TestDb()
    {
        Paths = new AppPaths(Path.Combine(Path.GetTempPath(), "ScreenTime.Tests", Guid.NewGuid().ToString("N")));
        Connection = Db.Open(Paths);
        Sessions = new SessionStore(Connection);
    }

    public AppPaths Paths { get; }
    public SqliteConnection Connection { get; private set; }
    public SessionStore Sessions { get; private set; }

    public void Reopen()
    {
        Connection.Dispose();
        Connection = Db.Open(Paths);
        Sessions = new SessionStore(Connection);
    }

    /// <summary>Records a finished session for <paramref name="processName"/> from start to end (Unix seconds).</summary>
    public void AddSession(string processName, long start, long end)
    {
        var app = new ForegroundApp(processName, processName, null);
        var id = Sessions.Open(app, start);
        Sessions.UpdateEnd(id, end);
    }

    public void Dispose()
    {
        Connection.Dispose();
        try { Directory.Delete(Paths.DataFolder, recursive: true); }
        catch (IOException) { }
    }
}

internal static class Time
{
    public static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
    public static readonly TimeZoneInfo India = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");

    /// <summary>Unix seconds for a wall-clock time in <paramref name="zone"/>.</summary>
    public static long Local(TimeZoneInfo zone, int year, int month, int day, int hour = 0, int minute = 0)
    {
        var local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone)).ToUnixTimeSeconds();
    }
}

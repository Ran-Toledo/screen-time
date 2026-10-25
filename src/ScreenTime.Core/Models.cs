namespace ScreenTime.Core;

/// <summary>The app that owns the foreground window (R6–R8).</summary>
public sealed record ForegroundApp(string ProcessName, string DisplayName, string? ExePath);

/// <summary>Where the tracker engine writes sessions. Implemented by <see cref="SessionStore"/>.</summary>
public interface ISessionSink
{
    /// <summary>Starts a session with end = start and returns its id.</summary>
    long Open(ForegroundApp app, long startUtc);

    /// <summary>Sets the session's end. Returns false if the session no longer exists.</summary>
    bool UpdateEnd(long sessionId, long endUtc);
}

/// <summary>One app's screen time within a period.</summary>
public sealed record AppUsage(string ProcessName, string DisplayName, TimeSpan Duration, double Share);

/// <summary>Everything the dashboard shows for one day (D3).</summary>
public sealed record DaySummary(
    DateOnly Day,
    TimeSpan Total,
    IReadOnlyList<AppUsage> Apps,
    IReadOnlyList<TimeSpan> Hours);

public sealed record DailyTotal(DateOnly Day, TimeSpan Total);

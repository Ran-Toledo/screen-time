using ScreenTime.Core;

namespace ScreenTime.Tracker;

/// <summary>
/// The screen-time rules (REQUIREMENTS §4, DESIGN §4.4). Pure: all times are passed in as
/// UTC Unix seconds and all writes go through <see cref="ISessionSink"/>, so it can be tested
/// without Windows. A tick at time t stands for the second [t, t+1).
/// </summary>
/// <param name="sink">Where sessions are written.</param>
/// <param name="warn">Receives a message when the open session had vanished from storage and was re-created.</param>
public sealed class TrackerEngine(ISessionSink sink, Action<string>? warn = null)
{
    /// <summary>A longer gap between ticks means the machine slept or froze (R14).</summary>
    public const int MaxTickGapSeconds = 5;

    /// <summary>An open session's end is written at least this often (R15).</summary>
    public const int FlushIntervalSeconds = 60;

    private OpenSession? _current;
    private long? _lastTickUtc;
    private bool _locked;
    private bool _suspended;

    /// <summary>Paused from the tray menu (R2). Takes effect on the next tick.</summary>
    public bool Paused { get; set; }

    public int IdleThresholdSeconds { get; set; } = SettingsStore.DefaultIdleThresholdSeconds;

    /// <summary>The app currently being timed, or null when no time is being counted.</summary>
    public string? CurrentProcessName => _current?.App.ProcessName;

    /// <param name="nowUtc">The current time.</param>
    /// <param name="lastInputUtc">When the last keyboard or mouse input happened.</param>
    /// <param name="app">The foreground app, or null when Windows reports no foreground window.</param>
    public void Tick(long nowUtc, long lastInputUtc, ForegroundApp? app)
    {
        // R14: after sleep, a freeze or a clock change, end at the last second we know was counted.
        if (_lastTickUtc is long lastTick && (nowUtc - lastTick > MaxTickGapSeconds || nowUtc < lastTick))
            Close(lastTick);

        if (Paused || _locked || _suspended)
        {
            Close(nowUtc); // R2, R3
        }
        else if (nowUtc - lastInputUtc >= IdleThresholdSeconds)
        {
            // R4: the idle wait is never counted, so end at the last input.
            if (_current is not null)
                Close(Math.Max(_current.StartUtc, lastInputUtc));
        }
        else if (app is null)
        {
            // R5: brief "no foreground window" moments keep the current app.
        }
        else if (_current is null)
        {
            Open(app, nowUtc);
        }
        else if (!string.Equals(_current.App.ProcessName, app.ProcessName, StringComparison.OrdinalIgnoreCase))
        {
            Close(nowUtc);
            Open(app, nowUtc);
        }

        if (_current is not null && nowUtc - _current.LastFlushUtc >= FlushIntervalSeconds)
        {
            WriteEnd(_current, nowUtc);
            _current.LastFlushUtc = nowUtc;
        }

        _lastTickUtc = nowUtc;
    }

    /// <summary>Session locked / switched away (true) or unlocked / back (false) (R3).</summary>
    public void SetLocked(bool locked, long nowUtc)
    {
        if (locked)
            Close(nowUtc);
        _locked = locked;
    }

    /// <summary>
    /// Sleep / hibernate (true) or resume (false) (R3). Kept apart from <see cref="SetLocked"/>
    /// so resuming onto the lock screen doesn't count as an unlock.
    /// </summary>
    public void SetSuspended(bool suspended, long nowUtc)
    {
        if (suspended)
            Close(nowUtc);
        _suspended = suspended;
    }

    /// <summary>Closes the current session before exit, logoff or shutdown (R16).</summary>
    public void Stop(long nowUtc) => Close(nowUtc);

    private void Open(ForegroundApp app, long nowUtc)
    {
        var id = sink.Open(app, nowUtc);
        _current = new OpenSession(id, app, nowUtc);
    }

    private void Close(long endUtc)
    {
        if (_current is null)
            return;

        // Cleared first: if the write fails we lose at most the time since the last flush,
        // instead of retrying a broken close on every tick.
        var session = _current;
        _current = null;
        WriteEnd(session, Math.Max(endUtc, session.StartUtc));
    }

    /// <summary>
    /// Writes the session's end. If its row has disappeared (the database was reset or rolled
    /// back underneath us), re-creates it from the original start so the time isn't lost.
    /// </summary>
    private void WriteEnd(OpenSession session, long endUtc)
    {
        if (sink.UpdateEnd(session.Id, endUtc))
            return;

        var lostId = session.Id;
        session.Id = sink.Open(session.App, session.StartUtc);
        sink.UpdateEnd(session.Id, endUtc);
        warn?.Invoke($"Session {lostId} ({session.App.ProcessName}) was missing from the database; re-created it as session {session.Id}.");
    }

    private sealed class OpenSession(long id, ForegroundApp app, long startUtc)
    {
        public long Id { get; set; } = id;
        public ForegroundApp App { get; } = app;
        public long StartUtc { get; } = startUtc;
        public long LastFlushUtc { get; set; } = startUtc;
    }
}

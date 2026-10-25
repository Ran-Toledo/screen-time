# ScreenTime — System Design

**Status:** Proposed · **Date:** 2026-10-25 · **Implements:** [REQUIREMENTS.md](REQUIREMENTS.md)

## 1. Overview

```
┌────────────────────────────┐   writes sessions    ┌──────────────────────────────┐   reads sessions     ┌──────────────────────────┐
│ ScreenTime.Tracker.exe     │ ───────────────────▶ │ %LOCALAPPDATA%\ScreenTime\   │ ◀─────────────────── │ ScreenTime.Dashboard.exe │
│ tray icon, starts at login │ ◀─── reads settings  │   screentime.db  (SQLite WAL)│ ─── writes settings ▶│ opened on demand         │
└────────────────────────────┘                      └──────────────────────────────┘                      └──────────────────────────┘
              │                                                  ▲
              └──────────────── both use ScreenTime.Core ────────┘
```

- The two programs communicate **only through the SQLite file**: no ports, pipes or other IPC.
- The tracker writes sessions and reads settings. The dashboard reads sessions and writes settings.
- SQLite in **WAL mode** lets the dashboard read while the tracker writes.

### Key design choices

| Choice | Why |
|---|---|
| Per-user tray app, **not a Windows Service** | Services run in Session 0 and can't see the user's foreground window. |
| **Poll once per second** instead of event hooks | One timer and one tick function hold all the logic. Cost is negligible. |
| `Microsoft.Win32.SystemEvents` for lock / sleep / logoff | Built into .NET, so no extra native code. |
| Pure `TrackerEngine` separated from Windows calls | The screen-time rules can be unit-tested with fake inputs and a fake clock. |
| WinForms for both apps | The simplest native UI on .NET. The tracker needs a message loop for the tray and system events anyway. |
| Store only active sessions | Idle/locked time isn't shown anywhere, so it isn't stored. |
| Aggregate in C#, not SQL | Splitting at midnight and DST hours is easier to write and test in C#. Data volumes are small. |

## 2. Solution layout

```
ScreenTime.sln
src/
  ScreenTime.Core/            class library   net9.0-windows
    AppPaths.cs               data folder + db path
    Db.cs                     open connection, PRAGMAs, schema creation
    SessionStore.cs           tracker writes: get/create app, insert/update session
    SettingsStore.cs          get/set idle threshold
    Stats.cs                  dashboard reads: day totals, per-app, hourly, last 7 days
    Models.cs                 AppUsage, DaySummary, etc.
  ScreenTime.Tracker/         WinForms WinExe net9.0-windows
    Program.cs                single-instance mutex, run TrayContext
    TrayContext.cs            NotifyIcon + menu, 1 s timer, SystemEvents wiring
    TrackerEngine.cs          pure screen-time state machine (no Win32)
    ForegroundAppProbe.cs     foreground window → process name + display name
    IdleProbe.cs              GetLastInputInfo → last input time
    StartupRegistration.cs    HKCU Run key on/off
    Native.cs                 P/Invoke declarations
  ScreenTime.Dashboard/       WinForms WinExe net9.0-windows
    Program.cs
    MainForm.cs               date navigation, totals, list, charts
    SettingsForm.cs           idle threshold
tests/
  ScreenTime.Tests/           xUnit: TrackerEngine + Stats
publish.ps1                   builds both exes into one folder
```

**Dependencies (NuGet)**
- `Microsoft.Data.Sqlite`: Core.
- `ScottPlot.WinForms` (v5): Dashboard charts.
- `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`: tests.

## 3. Data model

File: `%LOCALAPPDATA%\ScreenTime\screentime.db`. Opened with `PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;`.

```sql
CREATE TABLE IF NOT EXISTS apps (
  id            INTEGER PRIMARY KEY,
  process_name  TEXT NOT NULL UNIQUE COLLATE NOCASE,   -- R7: identity, e.g. 'chrome'
  display_name  TEXT NOT NULL,                         -- R8: e.g. 'Google Chrome'
  exe_path      TEXT NULL                              -- last seen path; may be null (elevated)
);

CREATE TABLE IF NOT EXISTS sessions (
  id         INTEGER PRIMARY KEY,
  app_id     INTEGER NOT NULL REFERENCES apps(id),
  start_utc  INTEGER NOT NULL,     -- Unix seconds, UTC (R13)
  end_utc    INTEGER NOT NULL      -- Unix seconds, UTC; >= start_utc
);
CREATE INDEX IF NOT EXISTS ix_sessions_start ON sessions(start_utc);

CREATE TABLE IF NOT EXISTS settings (
  key    TEXT PRIMARY KEY,
  value  TEXT NOT NULL
);
-- keys: 'idle_threshold_seconds' (default 300, range 60..1800)

PRAGMA user_version = 1;           -- schema version for future migrations
```

- `Db.Open()` creates the folder, file and schema if missing. Both apps call it, and it is safe to run every time.
- When a session starts, `start_utc = end_utc = start`. Its `end_utc` is then updated as it grows (R15).
- **Overlap query** used by stats for a range `[from, to)`:
  `SELECT a.process_name, a.display_name, s.start_utc, s.end_utc FROM sessions s JOIN apps a ON a.id = s.app_id WHERE s.start_utc < @to AND s.end_utc > @from`.
  Sessions are short (minutes), so the start index is enough. Stats clamp each session to the range in C#.

## 4. Tracker

### 4.1 Process lifecycle

1. `Program.Main` takes the named mutex `Local\ScreenTime.Tracker`. If it's already held, it exits immediately (R19).
2. It opens the database. On the first run with no `startup_initialized` setting, it enables the Run key and stores that setting (R20).
3. It runs `TrayContext`, an `ApplicationContext` with no visible form, a `NotifyIcon` and a 1-second `System.Windows.Forms.Timer`.
4. On exit (menu Exit, `SessionEnding`, `ApplicationExit`), it calls `engine.Stop(now)` to close and save the session (R16), hides the icon and disposes everything.

### 4.2 Tray menu (R22)

| Item | Action |
|---|---|
| Open Dashboard | `Process.Start` of `ScreenTime.Dashboard.exe` from the tracker's own folder |
| Pause tracking / Resume tracking | toggles `engine.Paused` (R2); the tooltip shows "ScreenTime: paused" |
| Start at login ✓ | toggles the HKCU Run value `ScreenTime` = `"<folder>\ScreenTime.Tracker.exe"` |
| Exit | stop the engine and exit |

### 4.3 Inputs (Windows probes)

**`IdleProbe.GetLastInputUtc(now)`**
- Calls `GetLastInputInfo`. Idle milliseconds = `unchecked((uint)Environment.TickCount - info.dwTime)`; the unsigned subtraction handles the 49.7-day tick counter wrap.
- Returns `now - idle`.

**`ForegroundAppProbe.GetForegroundApp()`** returns `(ProcessName, DisplayName, ExePath)` or `null`:
1. `hwnd = GetForegroundWindow()`. If it's zero, return `null` (R5).
2. `GetWindowThreadProcessId(hwnd, out pid)`.
3. **UWP (R9):** if the process is `ApplicationFrameHost`, `EnumChildWindows(hwnd)` and take the first child whose PID differs. Fall back to the host if none is found.
4. `Process.GetProcessById(pid).ProcessName`. This works for elevated processes too (R11). If the process has already exited, return `null`.
5. Display name, cached per process name for the tracker's lifetime:
   - `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` + `QueryFullProcessImageName` to get the exe path.
   - `FileVersionInfo.GetVersionInfo(path).FileDescription` when it's non-empty, otherwise the process name (R8).
   - `explorer.exe` reports "Windows Explorer" this way, which satisfies R10 without special-casing.
6. Any exception means `null` for this sample (N6).

**Session state (R3)** comes from `SystemEvents`, which `TrayContext` turns into calls on the engine:

| Event | Engine call |
|---|---|
| `SessionSwitch`: `SessionLock`, `ConsoleDisconnect`, `RemoteDisconnect`, `SessionLogoff` | `engine.SetLocked(true, now)` |
| `SessionSwitch`: `SessionUnlock`, `ConsoleConnect`, `RemoteConnect`, `SessionLogon` | `engine.SetLocked(false, now)` |
| `PowerModeChanged`: `Suspend` | `engine.SetSuspended(true, now)` |
| `PowerModeChanged`: `Resume` | `engine.SetSuspended(false, now)` |
| `SessionEnding` | `engine.Stop(now)` |

Locked and suspended are tracked **separately**. The PC usually locks before it sleeps, and the resume event must not count as an unlock while the lock screen is still showing.

### 4.4 `TrackerEngine`: the screen-time state machine

The engine is pure: it takes `now` and input samples as arguments and writes through an `ISessionSink` interface. `SessionStore` implements the interface in production, and a fake records calls in tests.

```csharp
interface ISessionSink {
    long Open(string processName, string displayName, string? exePath, long startUtc); // returns session id
    void UpdateEnd(long sessionId, long endUtc);
}

sealed class TrackerEngine {
    public bool Paused { get; set; }
    public int IdleThresholdSeconds { get; set; } = 300;
    public void Tick(long nowUtc, long lastInputUtc, ForegroundApp? app);
    public void SetLocked(bool locked, long nowUtc);
    public void SetSuspended(bool suspended, long nowUtc);
    public void Stop(long nowUtc);
}
```

**State:** `current` (session id, process name, start), `lastTickUtc`, `lastFlushUtc`, `locked`, `suspended`.

**`Tick(now, lastInput, app)`**, in this order:

1. **Gap check (R14):** if `current != null` and (`now - lastTickUtc > 5` or `now < lastTickUtc`), close `current` at `lastTickUtc`.
2. **Work out whether this second is active:**
   - If `Paused`, `locked` or `suspended`, it's not active (R2, R3). Close `current` at `now`.
   - If `now - lastInput >= IdleThresholdSeconds`, the user is idle (R4). Close `current` at `max(current.start, lastInput)`. Time after the last input is never counted.
   - If `app == null` (R5), keep `current` as it is. If there is no `current`, do nothing.
   - Otherwise it's active on `app`.
3. **When active (R6, R12):**
   - If there is no `current`, open a session for `app` at `now`.
   - If `current` is a different app, close `current` at `now` and open a new session for `app` at `now`.
   - If it's the same app, the session continues.
4. **Flush (R15):** if `current != null` and `now - lastFlushUtc >= 60`, `UpdateEnd(current, now)` and set `lastFlushUtc = now`.
5. Set `lastTickUtc = now`.

**Closing** a session at time `t` calls `UpdateEnd(id, t)` and clears `current`. It's done one way everywhere.

- `SetLocked(true, now)` closes `current` at `now` and sets `locked`. `SetLocked(false, now)` only clears `locked`; the next tick opens a new session. `SetSuspended` works the same way with `suspended`.
- `Stop(now)` closes `current` at `now`.

**Invariant R1** holds by construction: at most one session is open at any time, and sessions never overlap, because a new one opens only at or after the previous one's close time.

### 4.5 Settings reload (D6)

Every 60 ticks, `TrayContext` reads `idle_threshold_seconds`, clamps it to 60–1800, and sets `engine.IdleThresholdSeconds`.

### 4.6 Error handling (N6)

- Every tick runs in a `try/catch`. Exceptions are written to `%LOCALAPPDATA%\ScreenTime\tracker.log`, which is capped at about 1 MB and rolled over by renaming to `.old`. The tracker then continues.
- Database busy errors are absorbed by `busy_timeout`. If a write still fails, that sample is skipped and the next flush catches up.

## 5. Dashboard

### 5.1 Window layout

```
┌───────────────────────────────────────────────────────────────────────────┐
│  [◀] [ Sun 25 Oct 2026 ▾ ] [▶]  [Today]                [Refresh] [⚙]      │
├───────────────────────────────────────────────────────────────────────────┤
│  Screen time today:  5h 42m                                               │
├───────────────────────────────┬───────────────────────────────────────────┤
│  App               Time    %  │  Top apps (horizontal bar chart, top 10)  │
│  Google Chrome   2h 10m  38%  │                                           │
│  Visual Studio…  1h 55m  34%  │                                           │
│  Windows Explorer  12m    4%  │                                           │
│  …                            ├───────────────────────────────────────────┤
│  (ListView, all apps)         │  By hour (24 bars, minutes 0–60)          │
│                               ├───────────────────────────────────────────┤
│                               │  Last 7 days (7 bars, hours)              │
└───────────────────────────────┴───────────────────────────────────────────┘
```

- **D2:** the arrows step one day; the `DateTimePicker` jumps to any date; Today resets to today. ▶ is disabled on today.
- **D5:** a 60-second `Timer` reloads the data while today is selected, and Refresh reloads on demand.
- **D6:** ⚙ opens `SettingsForm`, a `NumericUpDown` for 1–30 minutes that saves to `settings`.
- **D7:** with no rows, it shows "No screen time recorded for this day", empty charts and a total of 0m.

### 5.2 Stats (`ScreenTime.Core.Stats`)

```csharp
DaySummary GetDay(DateOnly day);           // total, per-app list, 24 hourly buckets
IReadOnlyList<(DateOnly Day, TimeSpan Total)> GetDailyTotals(DateOnly lastDay, int days);
```

- **Day bounds (R17, R18):** `from = local midnight of day`, `to = local midnight of day+1`, each converted to UTC with `TimeZoneInfo.Local`. A DST day therefore spans 23 or 25 hours.
- **Per-app:** fetch overlapping sessions, clamp each to `[from, to)`, and sum by `process_name`. Sort by time descending. The share is the app's time divided by the day's total.
- **Hourly:** 24 buckets indexed by local hour 0–23. Each clamped session is walked forward one piece at a time:
  ```
  t = start
  while t < end:
      local = ToLocal(t)
      next  = min(end, t + 3600 - (local.Minute*60 + local.Second))
      bucket[local.Hour] += next - t
      t = next
  ```
  - This handles DST and half-hour time zones. On a 23-hour day the skipped hour stays empty. On a 25-hour day the repeated hour adds to the same bucket.
- **Last 7 days:** call the day total 7 times. Each call is one indexed query, so this is cheap.

The dashboard opens a fresh connection for each refresh and closes it afterwards, so it never holds locks.

## 6. Build and deployment

- `publish.ps1` runs `dotnet publish` for both apps with `-c Release -r win-x64 --self-contained false -p:PublishSingleFile=true` into a single `publish\` folder.
- To install, copy `publish\` to `%LOCALAPPDATA%\Programs\ScreenTime\` and run `ScreenTime.Tracker.exe` once. That registers start-at-login.
- To uninstall, untick "Start at login", choose Exit, then delete the program folder and `%LOCALAPPDATA%\ScreenTime\`.
- It requires the .NET 9 Desktop Runtime, which is already present with the installed SDK 9.0.318.

## 7. Requirements traceability

| Requirement | Where |
|---|---|
| R1, R6, R12 | `TrackerEngine.Tick` steps 2–3; single open session invariant |
| R2 | `TrackerEngine.Paused`; tray Pause/Resume |
| R3 | `SystemEvents` → `SetLocked` / `SetSuspended` |
| R4 | `IdleProbe` + `Tick` idle branch (closes at last input); `SettingsForm` |
| R5 | `ForegroundAppProbe` returns null → `Tick` keeps current |
| R7, R8, R10, R11 | `ForegroundAppProbe` (process name, FileDescription); `apps` table |
| R9 | `ForegroundAppProbe` ApplicationFrameHost child lookup |
| R13 | Unix-second UTC columns |
| R14 | `Tick` step 1 gap check |
| R15 | `Tick` step 4 flush + insert on open |
| R16 | `Stop` on Exit / SessionEnding / ApplicationExit |
| R17, R18 | `Stats` day/hour bounds via `TimeZoneInfo.Local` |
| R19 | named mutex |
| R20, R22 | `StartupRegistration`, `TrayContext` |
| R21, N3 | `AppPaths` under `%LOCALAPPDATA%` |
| D1–D7 | `MainForm`, `SettingsForm`, `Stats` |
| N1 | 1 s timer, cached display names, no hooks or threads |
| N2 | titles are never read; no network code |
| N4 | `publish.ps1` single folder |
| N6 | per-tick try/catch, log file, `busy_timeout` |

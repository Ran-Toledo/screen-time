# ScreenTime — Implementation Plan

**Date:** 2026-10-25 · **Builds:** [DESIGN.md](DESIGN.md) · **Satisfies:** [REQUIREMENTS.md](REQUIREMENTS.md)

The work is split into six phases. Each one ends in something that builds and can be checked before moving on. The screen-time rules are built and unit-tested first, before any Windows-specific code, because that's where correctness matters most.

**Toolchain:** .NET SDK 9.0.318 (installed). Every project targets `net9.0-windows`. A `global.json` pins the SDK to `9.0.318` with `rollForward: latestFeature`, so builds don't pick up other SDKs.

---

## Phase 0: Scaffold

**Tasks**
1. `git init` and add a `.gitignore` (`bin/`, `obj/`, `publish/`, `*.user`).
2. Add a `global.json` that pins SDK 9.0.318.
3. Create the solution and projects:
   - `src/ScreenTime.Core` (classlib, `net9.0-windows`)
   - `src/ScreenTime.Tracker` (winforms)
   - `src/ScreenTime.Dashboard` (winforms)
   - `tests/ScreenTime.Tests` (xunit)
4. Wire up references: Tracker, Dashboard and Tests → Core; Tests → Tracker, to reach `TrackerEngine`.
5. Add packages: `Microsoft.Data.Sqlite` → Core, `ScottPlot.WinForms` → Dashboard.
6. Add a shared `Directory.Build.props`: `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true`.

**Done when:** `dotnet build` and `dotnet test` succeed on the empty solution.

---

## Phase 1: Core data layer

**Tasks**
1. `AppPaths`: data folder `%LOCALAPPDATA%\ScreenTime`, db path, log path. It accepts an override folder so tests can use a temp directory.
2. `Db.Open(path)`: creates the folder, applies the PRAGMAs (WAL, `busy_timeout=5000`, `foreign_keys=ON`), and runs the schema script if `user_version = 0`.
3. `SessionStore` (implements `ISessionSink`):
   - `GetOrCreateApp`, which caches the process-name → id lookup and updates the display name and path when they change.
   - `Open`, which inserts a session with `start = end`.
   - `UpdateEnd`.
4. `SettingsStore`:
   - `GetIdleThresholdSeconds()` returns 300 by default, clamped to 60–1800.
   - `SetIdleThresholdSeconds()`.
   - A generic `Get`/`Set` for `startup_initialized`.
5. `Stats`:
   - `GetDay(DateOnly)` returns the total, the per-app list and 24 hourly buckets.
   - `GetDailyTotals(DateOnly lastDay, int days)`.
   - It takes a `TimeZoneInfo` parameter, defaulting to `Local`, so tests can use fixed zones.

**Tests** (temp-folder database)
- The schema is created once, and reopening the database is a no-op.
- `GetOrCreateApp` matches names case-insensitively (`Chrome` = `chrome`, R7).
- A day's total equals the sum of its per-app times (R1).
- A session from 23:50 to 00:10 gives 10 minutes to each day (R17).
- On a DST spring-forward day (e.g. `America/New_York`, 2026-03-08), the day spans 23 hours, the hourly buckets sum to the day total, and the skipped hour is 0 (R18).
- On a DST fall-back day (2026-11-01), the day spans 25 hours and the repeated hour holds up to 120 minutes.
- In a half-hour zone (e.g. `India Standard Time`), the hourly buckets sum to the total.
- The settings clamp works: 10 → 60 and 5000 → 1800.

**Done when:** all tests pass.

---

## Phase 2: Screen-time engine (pure logic)

**Tasks**
1. Define the `ForegroundApp` record `(ProcessName, DisplayName, ExePath?)` and the `ISessionSink` interface.
2. Implement `TrackerEngine` exactly as in DESIGN §4.4: `Tick`, `SetLocked`, `SetSuspended`, `Stop`, `Paused`, `IdleThresholdSeconds`.

**Tests**, using a fake sink that records `Open`/`UpdateEnd`. Each scenario feeds one tick per simulated second.

| # | Scenario | Expected | Req |
|---|---|---|---|
| 1 | 10 s on A | one session A, 10 s | R6, R12 |
| 2 | A 10 s → B 5 s → A 5 s | three sessions; total 20 s; no overlap | R1, R12 |
| 3 | A, then no input for 5 min (threshold 300) | session ends at last input time | R4 |
| 4 | idle, then input resumes on A | new session opens at resume time | R4 |
| 5 | threshold changed to 60 s mid-run | idle detected after 60 s | R4, D6 |
| 6 | foreground null for 3 ticks during A | A continues uninterrupted | R5 |
| 7 | foreground null with no current session | nothing is opened | R5 |
| 8 | `SetLocked(true)` then 60 s of ticks then unlock | session closed at lock; nothing during lock; new session after | R3 |
| 9 | Paused for 30 s | nothing recorded while paused | R2 |
| 10 | tick gap of 120 s (simulated sleep) | session closed at last good tick | R14 |
| 11 | clock goes backwards 1 h | session closed at last good tick; new one starts | R14 |
| 12 | 150 s continuous on A | `UpdateEnd` called at ≥60 s intervals | R15 |
| 13 | `Stop()` mid-session | session closed at stop time | R16 |
| 14 | last input earlier than session start, then idle | end is clamped to the session start (never negative) | R4 |
| 15 | lock → suspend → resume (still locked) | nothing recorded until unlock | R3 |

**Done when:** all scenarios pass.

---

## Phase 3: Tracker app (Windows plumbing)

**Tasks**
1. `Native.cs`: P/Invoke for `GetForegroundWindow`, `GetWindowThreadProcessId`, `EnumChildWindows`, `GetLastInputInfo`, `OpenProcess`, `QueryFullProcessImageName` and `CloseHandle`, using `LibraryImport`.
2. `IdleProbe`: last input time in UTC, with handling for the tick counter wrap.
3. `ForegroundAppProbe`: the steps in DESIGN §4.3, including the ApplicationFrameHost child lookup and the display-name cache.
4. `StartupRegistration`: `IsEnabled`, `Enable` and `Disable` on the `HKCU\...\Run` value `ScreenTime`.
5. `TrayContext`:
   - The `NotifyIcon` and menu (R22).
   - A 1 s timer that calls `engine.Tick`.
   - `SystemEvents` subscriptions (DESIGN §4.3), detached when the tracker exits.
   - Settings reload every 60 ticks.
   - Per-tick `try/catch` that writes to `tracker.log` (1 MB roll-over).
6. `Program.Main`: single-instance mutex (R19), first-run start-at-login (R20), then run `TrayContext`.
7. Tray icon: an embedded `.ico` (simple clock glyph) so it's recognizable in the tray.

**Manual checks** (inspect the database with a small `--dump-today` debug command in the tracker that prints today's sessions to the console, or with the dashboard once Phase 4 is done)
- [ ] Switching between three apps for about 30 s each gives three sessions with the right names.
- [ ] Clicking the desktop and taskbar records "Windows Explorer" (R10).
- [ ] Calculator records as Calculator, not ApplicationFrameHost (R9).
- [ ] Task Manager (elevated) records as Task Manager (R11).
- [ ] Leaving the PC alone for 6 minutes ends the session about 5 minutes before the idle was noticed, at the last input (R4).
- [ ] Locking for 2 minutes records nothing during the lock (R3).
- [ ] Sleep and resume closes the session at sleep time (R3, R14).
- [ ] Killing the tracker in Task Manager loses at most about 60 s; restarting continues cleanly (R15).
- [ ] A second launch exits immediately (R19).
- [ ] Pause and Resume work, and "Start at login" toggles the registry value (R2, R20).
- [ ] Task Manager shows the tracker near 0% CPU and under 50 MB of memory (N1).

---

## Phase 4: Dashboard

**Tasks**
1. Build the `MainForm` layout from DESIGN §5.1 with `TableLayoutPanel` / `SplitContainer` so it resizes cleanly.
2. Date navigation: ◀ / ▶ / `DateTimePicker` / Today. ▶ is disabled on today (D2).
3. Total label and the per-app `ListView` with name, time and % (D3a, D3b).
4. ScottPlot charts: top-10 apps as horizontal bars, 24 hourly bars, 7 daily bars (D3c, D3d, D4).
5. A 60 s auto-refresh while today is shown, plus the Refresh button (D5).
6. `SettingsForm`: idle threshold in minutes, 1–30 (D6).
7. Empty state when there's no data or no database file yet (D7).
8. Duration formatting helper: `5h 42m`, `12m`, `<1m`.

**Manual checks**
- [ ] The Today view matches the sessions the tracker just recorded.
- [ ] Stepping to a day with no data shows the empty state.
- [ ] The app list adds up to the total.
- [ ] Changing the idle threshold to 1 minute makes the tracker detect idle after 1 minute, without a restart.
- [ ] The dashboard opens and works while the tracker is not running (D1).
- [ ] The window resizes without overlapping controls.

---

## Phase 5: Packaging and end-to-end check

**Tasks**
1. `publish.ps1`: publishes both apps (framework-dependent, single-file, `win-x64`) into `publish\`.
2. `README.md`: what it is, install, uninstall, where the data lives, known limitations.
3. Install to `%LOCALAPPDATA%\Programs\ScreenTime\`, run the tracker, sign out and back in.

**Done when**
- [ ] The tracker starts automatically after signing in.
- [ ] Tray → Open Dashboard launches the dashboard.
- [ ] After about a day of normal use, the totals look plausible against your own sense of the day.

---

## Order and effort

| Phase | Depends on | Rough size |
|---|---|---|
| 0 Scaffold | — | small |
| 1 Core data layer | 0 | medium |
| 2 Engine | 0 | medium |
| 3 Tracker app | 1, 2 | medium |
| 4 Dashboard | 1 | medium |
| 5 Packaging | 3, 4 | small |

Phases 1 and 2 are independent of each other, and so are 3 and 4. Implementing them in order 0 → 1 → 2 → 3 → 4 → 5 is still the simplest path, and keeps each step testable.

## Risks

| Risk | Mitigation |
|---|---|
| ScottPlot 5 API differs from examples | Pin a specific 5.x version; the charts are simple bar plots |
| `ApplicationFrameHost` child lookup misses an app | Falls back to the host (R9); check with Calculator and Settings |
| `SystemEvents` needs a message loop | The WinForms `Application.Run` in the tracker provides one |
| Database locked during a dashboard read | WAL mode plus `busy_timeout`; the dashboard uses short-lived connections |

# ScreenTime

A small, private screen-time tracker for Windows.

- **ScreenTime.Tracker.exe** runs in the tray from login. Once a second it notes which app is in front, and whether you're actually there (not idle, locked or asleep).
- **ScreenTime.Dashboard.exe** is a desktop app you open when you want to look. It shows the day's total, time per app, an hour-by-hour chart and the last 7 days.

Only app names are recorded: never window titles, documents or URLs. Everything stays in one SQLite file on your PC, and nothing is sent anywhere.

## How screen time is counted

A second counts when tracking isn't paused, your session is unlocked and awake, and you've touched the keyboard or mouse within the idle limit (5 minutes by default, adjustable from 1 to 30 in the dashboard's Settings). It counts toward whichever app is in front. When you go idle, the session ends at your last input, so the idle wait itself is never counted.

The full rules are in [docs/REQUIREMENTS.md](docs/REQUIREMENTS.md) §4.

**Known limitations**
- Watching a video or sitting in a call without touching the keyboard or mouse counts as idle after the limit.
- Nothing is counted while the screen is locked.

## Requirements

- Windows 10 or 11, x64
- [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) (already present if the .NET 9 SDK is installed)

## Build

```powershell
.\publish.ps1
```

This runs the tests, then builds both apps into `publish\`: two exes plus the native SQLite and SkiaSharp libraries they need. Add `-SkipTests` to skip the tests, or `-Output <folder>` to build somewhere else.

## Install

```powershell
$dest = "$env:LOCALAPPDATA\Programs\ScreenTime"
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item .\publish\* $dest -Force
& "$dest\ScreenTime.Tracker.exe"
```

No admin rights are needed. On its first run the tracker turns on **Start at login**, through the current user's `Run` registry key.

Right-click the tray icon (a blue clock) for these options:

| Item | What it does |
|---|---|
| **Open Dashboard** | Opens the dashboard; double-clicking the icon does the same |
| **Pause / Resume tracking** | Stops counting until you resume; the icon turns grey |
| **Start at login** | Turns starting at login on or off |
| **Exit** | Saves the current session and quits |

To update, choose **Exit** from the tray menu, copy the new `publish\*` over the old files, and start the tracker again.

## Uninstall

1. In the tray menu, untick **Start at login**, then choose **Exit**.
2. Delete `%LOCALAPPDATA%\Programs\ScreenTime`.
3. To also delete your history, delete `%LOCALAPPDATA%\ScreenTime`.

## Where data lives

| File | Contents |
|---|---|
| `%LOCALAPPDATA%\ScreenTime\screentime.db` | All recorded sessions and settings (SQLite) |
| `%LOCALAPPDATA%\ScreenTime\tracker.log` | Tracker errors, if any (capped at about 1 MB) |

To see today's raw data without the dashboard, run the following. The tracker is a GUI app, so its output has to be redirected to a file:

```powershell
Start-Process "$env:LOCALAPPDATA\Programs\ScreenTime\ScreenTime.Tracker.exe" -ArgumentList '--dump-today' -Wait -NoNewWindow -RedirectStandardOutput dump.txt
Get-Content dump.txt
```

## Development

```powershell
dotnet build
dotnet test
```

- SDK: .NET 9 (pinned to 9.0.318 in `global.json`). Both apps use WinForms.
- **Debug builds use `%LOCALAPPDATA%\ScreenTime-Dev`** and never register start-at-login, so running from `bin\Debug` doesn't touch your real data or your startup apps.

| Project | Purpose |
|---|---|
| `src/ScreenTime.Core` | Database, settings, stats (day, hour and 7-day totals) |
| `src/ScreenTime.Tracker` | Tray app: Windows probes, system events, and `TrackerEngine` (the screen-time rules, which don't call Windows directly) |
| `src/ScreenTime.Dashboard` | Dashboard window and Settings dialog |
| `tests/ScreenTime.Tests` | xUnit tests for the engine, storage and stats |

Design documents: [requirements](docs/REQUIREMENTS.md) · [design](docs/DESIGN.md) · [implementation plan](docs/IMPLEMENTATION_PLAN.md)

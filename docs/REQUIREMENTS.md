# ScreenTime — System Requirements

**Status:** Approved for implementation · **Date:** 2026-10-25

## 1. Purpose

ScreenTime is a personal, Windows-only tool that records how much time the user actively spends at their computer and in which apps. It has two parts:

- **Tracker** — a lightweight background program that starts at login and records activity.
- **Dashboard** — a native desktop app the user opens on demand to view analytics.

All data stays on the local machine.

## 2. Scope

**In scope**
- Daily screen time and per-app time for the signed-in Windows user.
- Windows 10/11, x64.

**Out of scope**
- Window titles, document titles, URLs, or any content inside apps.
- macOS / Linux / cross-platform support.
- Network features, sync, cloud storage, accounts.
- Multiple-device aggregation.
- Limits, notifications, or blocking of apps.

## 3. Definitions

| Term | Meaning |
|---|---|
| **Active second** | A second in which all of R2–R5 hold. |
| **Screen time** | The total of active seconds in a period. |
| **App** | A running program, identified by its process name (e.g. `chrome`, `code`, `explorer`). |
| **Foreground app** | The app owning the window that currently has focus. |
| **Session** | A stored, contiguous stretch of active time attributed to one app (start, end). |
| **Idle threshold** | Time without keyboard/mouse input after which the user is idle. Default 5 minutes. |
| **Day** | Local midnight to the next local midnight, in the machine's current time zone. |

## 4. Screen-time logic (functional requirements)

### Definition
- **R1.** Screen time is the time the user is actively at the computer. Every active second is attributed to exactly one app, so **the sum of per-app times always equals total screen time** for any period.

### When a second counts
The tracker samples state once per second. A second is active only if **all** of the following hold:

- **R2.** Tracking is not paused by the user (tray menu).
- **R3.** The user's session is unlocked and connected to the console. Locking, switching user, remote disconnect, sleep, or hibernate stop the clock immediately.
- **R4.** The user is not idle: the time since the last keyboard or mouse input is less than the idle threshold.
  - The idle threshold defaults to **5 minutes** and is configurable from **1 to 30 minutes** in the dashboard's Settings.
  - When the user becomes idle, the current session ends at the **time of the last input**, not when the threshold is crossed. The idle wait is never counted.
  - There are **no exceptions** to the idle rule (no fullscreen, media, or call exceptions).
- **R5.** A foreground window exists. During brief moments when Windows reports no foreground window (e.g. mid-switch), the time stays with the app that was already active. If no app was active, the second is not counted.

### Which app gets the time
- **R6.** Each active second is attributed to the foreground app.
- **R7.** Apps are identified by process name, case-insensitively. Multiple windows or instances of the same program count as one app. Apps that update into new version folders keep their history.
- **R8.** Each app has a display name: the program file's description (e.g. "Google Chrome") when available, otherwise the process name.
- **R9.** Microsoft Store (UWP) apps hosted by `ApplicationFrameHost` are attributed to the actual hosted app (e.g. Calculator), not to the host. If the hosted app can't be determined, the time goes to `ApplicationFrameHost`.
- **R10.** The desktop, taskbar, Start menu and Alt-Tab switcher belong to `explorer` and are shown as **"Windows Explorer"**. This time is counted, not excluded.
- **R11.** Elevated (administrator) apps are attributed by process name like any other app.

### Recording
- **R12.** Consecutive active seconds on the same app are stored as one session. A session ends when the foreground app changes or when any of R2–R5 stops holding.
- **R13.** Times are stored in UTC with one-second resolution. Focus changes shorter than one second may not be recorded.
- **R14.** If more than 5 seconds pass between two samples, or the clock moves backwards (unannounced sleep, clock change, system freeze), the current session ends at the last good sample.
- **R15.** Crash safety: a session is written when it starts, and its end time is updated at least every 60 seconds. A crash or power loss loses at most ~60 seconds.
- **R16.** On logoff, shutdown, or tracker exit, the current session is closed and saved first.

### Days and totals
- **R17.** Totals are computed per day as defined in §3. Sessions crossing midnight are split at query time, not when stored.
- **R18.** Daylight saving days are 23 or 25 hours long, with no special handling. Stored UTC data is always displayed in the current time zone.

### Running
- **R19.** Only one tracker instance runs per user. Launching a second instance exits immediately.
- **R20.** The tracker starts at Windows login. This is enabled on first run and can be toggled from the tray menu.
- **R21.** Each Windows user has their own data file in their own profile.
- **R22.** The tracker shows a tray icon with: **Open Dashboard**, **Pause / Resume tracking**, **Start at login** (checkable), **Exit**.

## 5. Dashboard requirements

- **D1.** A native Windows desktop app, not a web app. It reads the tracker's database directly and works whether or not the tracker is running.
- **D2.** Shows one selected day at a time. It defaults to today; the user can step back and forward by day, pick a date, and jump back to today.
- **D3.** For the selected day it shows:
  - **D3a.** Total screen time (e.g. "5h 42m").
  - **D3b.** Time per app, sorted from most to least, with display name, duration and share of the day.
  - **D3c.** A bar chart of the top apps.
  - **D3d.** An hour-by-hour chart (24 bars) of active minutes.
- **D4.** Shows a bar chart of daily totals for the 7 days ending on the selected day.
- **D5.** When showing today, it refreshes automatically every 60 seconds and has a manual Refresh button.
- **D6.** A Settings dialog edits the idle threshold (1–30 minutes). The tracker applies changes within 60 seconds without restarting.
- **D7.** If there is no data yet, the dashboard shows an empty state instead of an error.

## 6. Non-functional requirements

- **N1. Lightweight:** the tracker averages under 1% CPU and under 50 MB of memory.
- **N2. Private:** no window titles or content are read or stored. Nothing is sent over the network.
- **N3. Local storage:** one SQLite file per user at `%LOCALAPPDATA%\ScreenTime\screentime.db`. Data is kept indefinitely; expected growth is a few MB per year.
- **N4. Simple install:** one folder holding both executables, with no installer or admin rights. It requires the .NET 9 Desktop Runtime, which the installed .NET 9 SDK already includes.
- **N5. Tech stack:** C# on .NET 9 (`net9.0-windows`), WinForms for both apps.
- **N6. Robust:** the tracker must never crash because of an unreadable process, a missing window, or a locked database. It skips that sample and continues.

## 7. Known limitations (accepted)

- Watching a video or sitting in a call without touching the keyboard or mouse counts as idle after the threshold.
- Time while the screen is locked is never counted, even if something is playing.
- Focus changes shorter than 1 second may be missed.
- Changing the time zone shifts how past days are grouped, because they're displayed in the current zone.

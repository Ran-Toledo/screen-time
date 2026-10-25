using System.Diagnostics;
using System.Drawing.Drawing2D;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using ScreenTime.Core;

namespace ScreenTime.Tracker;

/// <summary>The tracker's windowless UI: tray icon, 1-second timer and system events (DESIGN §4.1–4.5).</summary>
internal sealed class TrayContext : ApplicationContext
{
    private const int SettingsReloadTicks = 60;
    private const string DashboardExe = "ScreenTime.Dashboard.exe";

    private readonly SqliteConnection _db;
    private readonly SettingsStore _settings;
    private readonly TrackerEngine _engine;
    private readonly ForegroundAppProbe _probe = new();
    private readonly TrackerLog _log;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly TrayIcons _icons = new();
    private int _ticks;
    private bool _stopped;

    public TrayContext(AppPaths paths)
    {
        _log = new TrackerLog(paths.TrackerLogPath);
        _db = Db.Open(paths);
        _settings = new SettingsStore(_db);
        _engine = new TrackerEngine(new SessionStore(_db))
        {
            IdleThresholdSeconds = _settings.GetIdleThresholdSeconds(),
        };

        EnableStartupOnFirstRun();

        _pauseItem = new ToolStripMenuItem("Pause tracking", null, (_, _) => TogglePause());
        _startupItem = new ToolStripMenuItem("Start at login", null, (_, _) => ToggleStartup())
        {
            Checked = StartupRegistration.IsEnabled(),
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open Dashboard", null, (_, _) => OpenDashboard()) { Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()));

        _tray = new NotifyIcon
        {
            Icon = _icons.Active,
            Text = "ScreenTime",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => OpenDashboard();

        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionEnding += OnSessionEnding;

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
        OnTick();
    }

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private void OnTick()
    {
        Guard(() =>
        {
            var now = Now;
            _engine.Tick(now, IdleProbe.GetLastInputUtc(now), _probe.GetForegroundApp());

            if (++_ticks % SettingsReloadTicks == 0)
                _engine.IdleThresholdSeconds = _settings.GetIdleThresholdSeconds();
        });
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        bool? locked = e.Reason switch
        {
            SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect
                or SessionSwitchReason.RemoteDisconnect or SessionSwitchReason.SessionLogoff => true,
            SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect
                or SessionSwitchReason.RemoteConnect or SessionSwitchReason.SessionLogon => false,
            _ => null,
        };
        if (locked is bool value)
            Guard(() => _engine.SetLocked(value, Now));
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
            Guard(() => _engine.SetSuspended(true, Now));
        else if (e.Mode == PowerModes.Resume)
            Guard(() => _engine.SetSuspended(false, Now));
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e) => Guard(() => _engine.Stop(Now));

    /// <summary>Runs engine work under one lock and never lets an exception escape (N6).</summary>
    private void Guard(Action action)
    {
        try
        {
            lock (_engine)
                action();
        }
        catch (Exception e)
        {
            _log.Write(e);
        }
    }

    private void TogglePause()
    {
        Guard(() => _engine.Paused = !_engine.Paused);
        var paused = _engine.Paused;
        _pauseItem.Text = paused ? "Resume tracking" : "Pause tracking";
        _tray.Icon = paused ? _icons.Paused : _icons.Active;
        _tray.Text = paused ? "ScreenTime (paused)" : "ScreenTime";
        OnTick(); // apply now rather than on the next timer tick
    }

    private void ToggleStartup()
    {
        try
        {
            if (StartupRegistration.IsEnabled())
                StartupRegistration.Disable();
            else
                StartupRegistration.Enable();
        }
        catch (Exception e)
        {
            _log.Write(e);
        }
        _startupItem.Checked = StartupRegistration.IsEnabled();
    }

    private void EnableStartupOnFirstRun()
    {
        try
        {
            if (_settings.Get(SettingsStore.StartupInitializedKey) is not null)
                return;
#if !DEBUG
            // Development builds run from bin\ and must not register themselves at login.
            StartupRegistration.Enable();
#endif
            _settings.Set(SettingsStore.StartupInitializedKey, "1");
        }
        catch (Exception e)
        {
            _log.Write(e);
        }
    }

    private void OpenDashboard()
    {
        var path = Path.Combine(AppContext.BaseDirectory, DashboardExe);
        if (!File.Exists(path))
        {
            MessageBox.Show($"{DashboardExe} was not found next to the tracker.", "ScreenTime",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e)
        {
            _log.Write(e);
        }
    }

    protected override void ExitThreadCore()
    {
        Shutdown();
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Shutdown();
        base.Dispose(disposing);
    }

    /// <summary>Closes the session (R16) and releases everything. Safe to call twice.</summary>
    private void Shutdown()
    {
        if (_stopped)
            return;
        _stopped = true;

        _timer.Stop();
        _timer.Dispose();
        Guard(() => _engine.Stop(Now));

        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionEnding -= OnSessionEnding;

        _tray.Visible = false;
        _tray.ContextMenuStrip?.Dispose();
        _tray.Dispose();
        _icons.Dispose();
        _db.Dispose();
    }

    /// <summary>Clock glyphs drawn at runtime: blue while tracking, grey while paused.</summary>
    private sealed class TrayIcons : IDisposable
    {
        private readonly nint _activeHandle;
        private readonly nint _pausedHandle;

        public TrayIcons()
        {
            (Active, _activeHandle) = Draw(Color.FromArgb(0x2B, 0x6C, 0xB0));
            (Paused, _pausedHandle) = Draw(Color.FromArgb(0x80, 0x80, 0x80));
        }

        public Icon Active { get; }
        public Icon Paused { get; }

        private static (Icon Icon, nint Handle) Draw(Color face)
        {
            using var bitmap = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var faceBrush = new SolidBrush(face);
                using var hands = new Pen(Color.White, 3.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.FillEllipse(faceBrush, 1, 1, 30, 30);
                g.DrawLine(hands, 16, 16, 16, 7);
                g.DrawLine(hands, 16, 16, 23, 19);
            }
            var handle = bitmap.GetHicon();
            return (Icon.FromHandle(handle), handle);
        }

        public void Dispose()
        {
            Active.Dispose();
            Paused.Dispose();
            Native.DestroyIcon(_activeHandle);
            Native.DestroyIcon(_pausedHandle);
        }
    }
}

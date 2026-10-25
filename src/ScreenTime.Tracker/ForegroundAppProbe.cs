using System.Diagnostics;
using ScreenTime.Core;

namespace ScreenTime.Tracker;

/// <summary>Finds the app that owns the foreground window (R5–R11, DESIGN §4.3). Never reads window titles.</summary>
internal sealed class ForegroundAppProbe
{
    private const string FrameHost = "ApplicationFrameHost";

    private readonly Dictionary<string, string> _displayNames = new(StringComparer.OrdinalIgnoreCase);

    // The foreground window rarely changes between ticks, so the last answer is reused.
    private nint _lastWindow;
    private uint _lastPid;
    private ForegroundApp? _lastApp;

    /// <summary>The foreground app, or null when there is no foreground window or it can't be identified.</summary>
    public ForegroundApp? GetForegroundApp()
    {
        var window = Native.GetForegroundWindow();
        if (window == 0)
            return null;

        Native.GetWindowThreadProcessId(window, out var pid);
        if (pid == 0)
            return null;

        if (window == _lastWindow && pid == _lastPid && _lastApp is not null)
            return _lastApp;

        var path = GetExePath(pid);
        var name = path is not null ? Path.GetFileNameWithoutExtension(path) : GetProcessName(pid);
        if (name is null)
            return null;

        var resolved = true;
        if (string.Equals(name, FrameHost, StringComparison.OrdinalIgnoreCase))
        {
            // R9: Store apps draw inside a frame owned by ApplicationFrameHost; the real app owns a child window.
            var hostedPid = FindHostedPid(window, pid);
            var hostedPath = hostedPid != 0 ? GetExePath(hostedPid) : null;
            var hostedName = hostedPath is not null ? Path.GetFileNameWithoutExtension(hostedPath)
                : hostedPid != 0 ? GetProcessName(hostedPid) : null;

            if (hostedName is not null)
                (name, path) = (hostedName, hostedPath);
            else
                resolved = false; // fall back to the host, but look again next tick
        }

        var app = new ForegroundApp(name, GetDisplayName(name, path), path);

        (_lastWindow, _lastPid, _lastApp) = resolved ? (window, pid, app) : (0, 0u, null);
        return app;
    }

    private string GetDisplayName(string name, string? path)
    {
        if (_displayNames.TryGetValue(name, out var cached))
            return cached;

        var display = name;
        if (path is not null)
        {
            try
            {
                var description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
                if (!string.IsNullOrEmpty(description))
                    display = description;
            }
            catch (Exception e) when (e is FileNotFoundException or UnauthorizedAccessException or IOException)
            {
            }
        }

        // Only cache once we've seen the path; an elevated first sighting shouldn't pin the fallback name.
        if (path is not null)
            _displayNames[name] = display;
        return display;
    }

    private static uint FindHostedPid(nint frameWindow, uint hostPid)
    {
        nint child = 0;
        while ((child = Native.FindWindowEx(frameWindow, child, null, null)) != 0)
        {
            Native.GetWindowThreadProcessId(child, out var childPid);
            if (childPid != 0 && childPid != hostPid)
                return childPid;
        }
        return 0;
    }

    private static unsafe string? GetExePath(uint pid)
    {
        var process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == 0)
            return null;

        try
        {
            const int Capacity = 1024;
            var buffer = stackalloc char[Capacity];
            var size = (uint)Capacity;
            return Native.QueryFullProcessImageName(process, 0, buffer, ref size)
                ? new string(buffer, 0, (int)size)
                : null;
        }
        finally
        {
            Native.CloseHandle(process);
        }
    }

    /// <summary>Fallback when the exe path is unavailable; works for elevated processes too (R11).</summary>
    private static string? GetProcessName(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return null; // exited between calls
        }
    }
}

namespace ScreenTime.Core;

/// <summary>Locations of the per-user data files (R21, N3).</summary>
public sealed class AppPaths
{
#if DEBUG
    // Development builds never touch the real data.
    private const string DefaultFolderName = "ScreenTime-Dev";
#else
    private const string DefaultFolderName = "ScreenTime";
#endif

    /// <param name="dataFolder">Override for tests; defaults to %LOCALAPPDATA%\ScreenTime (ScreenTime-Dev in Debug builds).</param>
    public AppPaths(string? dataFolder = null)
    {
        DataFolder = dataFolder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DefaultFolderName);
    }

    public string DataFolder { get; }

    public string DatabasePath => Path.Combine(DataFolder, "screentime.db");

    public string TrackerLogPath => Path.Combine(DataFolder, "tracker.log");
}

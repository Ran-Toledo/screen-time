namespace ScreenTime.Core;

/// <summary>Locations of the per-user data files (R21, N3).</summary>
public sealed class AppPaths
{
    /// <param name="dataFolder">Override for tests; defaults to %LOCALAPPDATA%\ScreenTime.</param>
    public AppPaths(string? dataFolder = null)
    {
        DataFolder = dataFolder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenTime");
    }

    public string DataFolder { get; }

    public string DatabasePath => Path.Combine(DataFolder, "screentime.db");

    public string TrackerLogPath => Path.Combine(DataFolder, "tracker.log");
}

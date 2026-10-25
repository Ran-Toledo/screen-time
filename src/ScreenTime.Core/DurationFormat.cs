namespace ScreenTime.Core;

public static class DurationFormat
{
    /// <summary>"5h 42m", "3h", "12m", "&lt;1m" or "0m". Minutes are rounded down.</summary>
    public static string Short(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            return "0m";
        if (duration < TimeSpan.FromMinutes(1))
            return "<1m";

        var totalMinutes = (long)duration.TotalMinutes;
        var (hours, minutes) = (totalMinutes / 60, totalMinutes % 60);
        return hours == 0 ? $"{minutes}m"
            : minutes == 0 ? $"{hours}h"
            : $"{hours}h {minutes}m";
    }
}

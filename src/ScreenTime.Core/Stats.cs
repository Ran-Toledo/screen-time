using Microsoft.Data.Sqlite;

namespace ScreenTime.Core;

/// <summary>Dashboard-side reads. Sessions are clamped and split by local day/hour here, not in storage (R17, R18).</summary>
public sealed class Stats(SqliteConnection connection, TimeZoneInfo? timeZone = null)
{
    private readonly TimeZoneInfo _timeZone = timeZone ?? TimeZoneInfo.Local;

    public DaySummary GetDay(DateOnly day)
    {
        var (from, to) = GetDayBounds(day);
        var sessions = LoadClamped(from, to);

        var hours = new long[24];
        var byApp = new Dictionary<string, (string DisplayName, long Seconds)>(StringComparer.OrdinalIgnoreCase);
        long total = 0;

        foreach (var s in sessions)
        {
            var length = s.End - s.Start;
            total += length;

            byApp[s.ProcessName] = byApp.TryGetValue(s.ProcessName, out var app)
                ? (app.DisplayName, app.Seconds + length)
                : (s.DisplayName, length);

            AddToHours(hours, s.Start, s.End);
        }

        var apps = byApp
            .Select(kv => new AppUsage(
                kv.Key,
                kv.Value.DisplayName,
                TimeSpan.FromSeconds(kv.Value.Seconds),
                total > 0 ? (double)kv.Value.Seconds / total : 0))
            .OrderByDescending(a => a.Duration)
            .ThenBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new DaySummary(
            day,
            TimeSpan.FromSeconds(total),
            apps,
            hours.Select(h => TimeSpan.FromSeconds(h)).ToList());
    }

    /// <summary>Totals for the <paramref name="days"/> days ending on <paramref name="lastDay"/>, oldest first (D4).</summary>
    public IReadOnlyList<DailyTotal> GetDailyTotals(DateOnly lastDay, int days)
    {
        var result = new List<DailyTotal>(days);
        for (var i = days - 1; i >= 0; i--)
        {
            var day = lastDay.AddDays(-i);
            var (from, to) = GetDayBounds(day);
            var seconds = LoadClamped(from, to).Sum(s => s.End - s.Start);
            result.Add(new DailyTotal(day, TimeSpan.FromSeconds(seconds)));
        }
        return result;
    }

    /// <summary>UTC Unix-second bounds [from, to) of a local day; 23 or 25 hours long on DST days.</summary>
    public (long From, long To) GetDayBounds(DateOnly day) =>
        (LocalMidnightToUnix(day), LocalMidnightToUnix(day.AddDays(1)));

    private long LocalMidnightToUnix(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);

        // A few zones skip midnight itself on DST day; the day then starts at the first valid local time.
        while (_timeZone.IsInvalidTime(local))
            local = local.AddMinutes(15);

        var utc = TimeZoneInfo.ConvertTimeToUtc(local, _timeZone);
        return new DateTimeOffset(utc).ToUnixTimeSeconds();
    }

    /// <summary>Adds [start, end) to local-hour buckets, one piece per local hour (DESIGN §5.2).</summary>
    private void AddToHours(long[] hours, long start, long end)
    {
        var t = start;
        while (t < end)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime, _timeZone);
            var next = Math.Min(end, t + 3600 - (local.Minute * 60 + local.Second));
            hours[local.Hour] += next - t;
            t = next;
        }
    }

    private List<ClampedSession> LoadClamped(long from, long to)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.process_name, a.display_name, s.start_utc, s.end_utc
            FROM sessions s JOIN apps a ON a.id = s.app_id
            WHERE s.start_utc < $to AND s.end_utc > $from;
            """;
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);

        var result = new List<ClampedSession>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ClampedSession(
                reader.GetString(0),
                reader.GetString(1),
                Math.Max(reader.GetInt64(2), from),
                Math.Min(reader.GetInt64(3), to)));
        }
        return result;
    }

    private readonly record struct ClampedSession(string ProcessName, string DisplayName, long Start, long End);
}

using ScreenTime.Core;

namespace ScreenTime.Tests;

public class StatsTests
{
    private static readonly TimeZoneInfo Eastern = Time.Eastern;

    [Fact]
    public void Empty_database_gives_an_empty_day()
    {
        using var db = new TestDb();

        var summary = new Stats(db.Connection, Eastern).GetDay(new DateOnly(2026, 10, 25));

        Assert.Equal(TimeSpan.Zero, summary.Total);
        Assert.Empty(summary.Apps);
        Assert.Equal(24, summary.Hours.Count);
        Assert.All(summary.Hours, h => Assert.Equal(TimeSpan.Zero, h));
    }

    [Fact]
    public void Per_app_times_add_up_to_the_total_and_are_sorted()
    {
        using var db = new TestDb();
        db.AddSession("code", Time.Local(Eastern, 2026, 10, 25, 9, 0), Time.Local(Eastern, 2026, 10, 25, 10, 0));
        db.AddSession("chrome", Time.Local(Eastern, 2026, 10, 25, 10, 0), Time.Local(Eastern, 2026, 10, 25, 10, 30));
        db.AddSession("code", Time.Local(Eastern, 2026, 10, 25, 10, 30), Time.Local(Eastern, 2026, 10, 25, 11, 15));

        var summary = new Stats(db.Connection, Eastern).GetDay(new DateOnly(2026, 10, 25));

        Assert.Equal(TimeSpan.FromMinutes(135), summary.Total);
        Assert.Equal(summary.Total, Sum(summary.Apps.Select(a => a.Duration)));
        Assert.Equal(summary.Total, Sum(summary.Hours));

        Assert.Collection(summary.Apps,
            a => { Assert.Equal("code", a.ProcessName); Assert.Equal(TimeSpan.FromMinutes(105), a.Duration); },
            a => { Assert.Equal("chrome", a.ProcessName); Assert.Equal(TimeSpan.FromMinutes(30), a.Duration); });
        Assert.Equal(1.0, summary.Apps.Sum(a => a.Share), precision: 9);

        Assert.Equal(TimeSpan.FromMinutes(60), summary.Hours[9]);
        Assert.Equal(TimeSpan.FromMinutes(60), summary.Hours[10]);
        Assert.Equal(TimeSpan.FromMinutes(15), summary.Hours[11]);
    }

    [Fact]
    public void Session_crossing_midnight_is_split_between_days()
    {
        using var db = new TestDb();
        db.AddSession("code", Time.Local(Eastern, 2026, 10, 24, 23, 50), Time.Local(Eastern, 2026, 10, 25, 0, 10));
        var stats = new Stats(db.Connection, Eastern);

        var saturday = stats.GetDay(new DateOnly(2026, 10, 24));
        var sunday = stats.GetDay(new DateOnly(2026, 10, 25));

        Assert.Equal(TimeSpan.FromMinutes(10), saturday.Total);
        Assert.Equal(TimeSpan.FromMinutes(10), saturday.Hours[23]);
        Assert.Equal(TimeSpan.FromMinutes(10), sunday.Total);
        Assert.Equal(TimeSpan.FromMinutes(10), sunday.Hours[0]);
    }

    [Fact]
    public void Spring_forward_day_is_23_hours_and_skipped_hour_is_empty()
    {
        using var db = new TestDb();
        var day = new DateOnly(2026, 3, 8);
        AddAllDay(db, day);

        var summary = new Stats(db.Connection, Eastern).GetDay(day);

        Assert.Equal(TimeSpan.FromHours(23), summary.Total);
        Assert.Equal(TimeSpan.Zero, summary.Hours[2]);
        Assert.Equal(summary.Total, Sum(summary.Hours));
    }

    [Fact]
    public void Fall_back_day_is_25_hours_and_repeated_hour_holds_two_hours()
    {
        using var db = new TestDb();
        var day = new DateOnly(2026, 11, 1);
        AddAllDay(db, day);

        var summary = new Stats(db.Connection, Eastern).GetDay(day);

        Assert.Equal(TimeSpan.FromHours(25), summary.Total);
        Assert.Equal(TimeSpan.FromHours(2), summary.Hours[1]);
        Assert.Equal(summary.Total, Sum(summary.Hours));
    }

    [Fact]
    public void Half_hour_time_zone_buckets_by_local_hour()
    {
        using var db = new TestDb();
        var india = Time.India;
        db.AddSession("code", Time.Local(india, 2026, 10, 25, 10, 15), Time.Local(india, 2026, 10, 25, 12, 45));

        var summary = new Stats(db.Connection, india).GetDay(new DateOnly(2026, 10, 25));

        Assert.Equal(TimeSpan.FromMinutes(150), summary.Total);
        Assert.Equal(TimeSpan.FromMinutes(45), summary.Hours[10]);
        Assert.Equal(TimeSpan.FromMinutes(60), summary.Hours[11]);
        Assert.Equal(TimeSpan.FromMinutes(45), summary.Hours[12]);
        Assert.Equal(summary.Total, Sum(summary.Hours));
    }

    [Fact]
    public void Daily_totals_cover_the_requested_days_oldest_first()
    {
        using var db = new TestDb();
        db.AddSession("code", Time.Local(Eastern, 2026, 10, 20, 9, 0), Time.Local(Eastern, 2026, 10, 20, 10, 0));
        db.AddSession("code", Time.Local(Eastern, 2026, 10, 25, 9, 0), Time.Local(Eastern, 2026, 10, 25, 9, 30));
        db.AddSession("code", Time.Local(Eastern, 2026, 10, 18, 9, 0), Time.Local(Eastern, 2026, 10, 18, 9, 30)); // outside range

        var totals = new Stats(db.Connection, Eastern).GetDailyTotals(new DateOnly(2026, 10, 25), 7);

        Assert.Equal(7, totals.Count);
        Assert.Equal(new DateOnly(2026, 10, 19), totals[0].Day);
        Assert.Equal(new DateOnly(2026, 10, 25), totals[6].Day);
        Assert.Equal(TimeSpan.FromHours(1), totals[1].Total);
        Assert.Equal(TimeSpan.FromMinutes(30), totals[6].Total);
        Assert.Equal(TimeSpan.FromMinutes(90), Sum(totals.Select(t => t.Total)));
    }

    /// <summary>A session from the previous day to the next, so the whole local day is covered.</summary>
    private static void AddAllDay(TestDb db, DateOnly day)
    {
        var before = Time.Local(Eastern, day.Year, day.Month, day.Day) - 3600;
        var after = Time.Local(Eastern, day.AddDays(1).Year, day.AddDays(1).Month, day.AddDays(1).Day) + 3600;
        db.AddSession("code", before, after);
    }

    private static TimeSpan Sum(IEnumerable<TimeSpan> values) =>
        values.Aggregate(TimeSpan.Zero, (acc, v) => acc + v);
}

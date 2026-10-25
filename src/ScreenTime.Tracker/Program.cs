using ScreenTime.Core;

namespace ScreenTime.Tracker;

static class Program
{
    private const string MutexName = @"Local\ScreenTime.Tracker";

    [STAThread]
    static int Main(string[] args)
    {
        var paths = new AppPaths();

        if (args is ["--dump-today"])
            return DumpToday(paths);

        // R19: one tracker per user; a second launch just exits.
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
            return 0;

        ApplicationConfiguration.Initialize();
        using var context = new TrayContext(paths);
        Application.Run(context);
        return 0;
    }

    /// <summary>Debug aid: prints today's totals and sessions to stdout (redirect it; this is a GUI exe).</summary>
    private static int DumpToday(AppPaths paths)
    {
        if (!File.Exists(paths.DatabasePath))
        {
            Console.WriteLine($"No database yet at {paths.DatabasePath}");
            return 1;
        }

        using var db = Db.OpenReadOnly(paths);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var summary = new Stats(db).GetDay(today);

        Console.WriteLine($"Database: {paths.DatabasePath}");
        Console.WriteLine($"Screen time {today:yyyy-MM-dd}: {summary.Total}");
        foreach (var app in summary.Apps)
            Console.WriteLine($"  {app.Duration,-10} {app.Share,5:P0}  {app.DisplayName} ({app.ProcessName})");

        var (from, to) = new Stats(db).GetDayBounds(today);
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT a.process_name, s.start_utc, s.end_utc
            FROM sessions s JOIN apps a ON a.id = s.app_id
            WHERE s.start_utc < $to AND s.end_utc > $from
            ORDER BY s.start_utc;
            """;
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);

        Console.WriteLine("Sessions:");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var start = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)).ToLocalTime();
            var end = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)).ToLocalTime();
            Console.WriteLine($"  {start:HH:mm:ss}-{end:HH:mm:ss}  {end - start,-10} {reader.GetString(0)}");
        }
        return 0;
    }
}

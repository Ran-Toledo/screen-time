using ScreenTime.Core;

namespace ScreenTime.Tests;

public class StoreTests
{
    [Fact]
    public void Schema_is_created_once_and_reopening_keeps_data()
    {
        using var db = new TestDb();
        db.AddSession("chrome", 100, 200);

        db.Reopen();

        Assert.Equal(Db.SchemaVersion, Db.GetUserVersion(db.Connection));
        using var command = db.Connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sessions;";
        Assert.Equal(1L, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public void Apps_are_matched_by_process_name_case_insensitively()
    {
        using var db = new TestDb();
        var first = db.Sessions.GetOrCreateApp(new ForegroundApp("Chrome", "Google Chrome", null));

        db.Reopen();
        var second = db.Sessions.GetOrCreateApp(new ForegroundApp("chrome", "Google Chrome", null));

        Assert.Equal(first, second);
    }

    [Fact]
    public void Display_name_and_path_are_refreshed_but_path_is_kept_when_unknown()
    {
        using var db = new TestDb();
        db.Sessions.GetOrCreateApp(new ForegroundApp("code", "code", @"C:\old\Code.exe"));
        db.Sessions.GetOrCreateApp(new ForegroundApp("code", "Visual Studio Code", null));

        using var command = db.Connection.CreateCommand();
        command.CommandText = "SELECT display_name, exe_path FROM apps WHERE process_name = 'code';";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("Visual Studio Code", reader.GetString(0));
        Assert.Equal(@"C:\old\Code.exe", reader.GetString(1));
    }

    [Fact]
    public void Open_starts_a_zero_length_session_and_UpdateEnd_extends_it()
    {
        using var db = new TestDb();
        var id = db.Sessions.Open(new ForegroundApp("notepad", "Notepad", null), 1_000);

        Assert.Equal((1_000L, 1_000L), ReadSession(db, id));

        db.Sessions.UpdateEnd(id, 1_060);
        Assert.Equal((1_000L, 1_060L), ReadSession(db, id));
    }

    [Fact] // the 2026-10-25 incident: ids reused after the tables were emptied underneath the tracker
    public void App_ids_stay_correct_when_the_database_changes_underneath()
    {
        using var db = new TestDb();
        var explorer = new ForegroundApp("explorer", "Windows Explorer", null);
        db.Sessions.Open(new ForegroundApp("chrome", "Google Chrome", null), 100);
        db.Sessions.Open(explorer, 200);

        // Another connection empties the tables and a different app takes id 1.
        using (var other = Db.Open(db.Paths))
        {
            using var wipe = other.CreateCommand();
            wipe.CommandText = "DELETE FROM sessions; DELETE FROM apps;";
            wipe.ExecuteNonQuery();
            new SessionStore(other).Open(new ForegroundApp("SearchHost", "SearchHost", null), 300);
        }

        var id = db.Sessions.Open(explorer, 400);

        using var command = db.Connection.CreateCommand();
        command.CommandText = "SELECT a.process_name FROM sessions s JOIN apps a ON a.id = s.app_id WHERE s.id = $id;";
        command.Parameters.AddWithValue("$id", id);
        Assert.Equal("explorer", command.ExecuteScalar());
    }

    [Fact]
    public void UpdateEnd_reports_whether_the_session_exists()
    {
        using var db = new TestDb();
        var id = db.Sessions.Open(new ForegroundApp("notepad", "Notepad", null), 1_000);

        Assert.True(db.Sessions.UpdateEnd(id, 1_010));
        Assert.False(db.Sessions.UpdateEnd(id + 100, 1_010));
    }

    [Fact]
    public void Read_only_connection_sees_tracker_writes_but_cannot_write()
    {
        using var db = new TestDb();
        db.AddSession("chrome", 100, 200);

        using var reader = Db.OpenReadOnly(db.Paths);
        using var count = reader.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM sessions;";
        Assert.Equal(1L, (long)count.ExecuteScalar()!);

        // The writer keeps going while the reader is open.
        db.AddSession("code", 200, 300);
        Assert.Equal(2L, (long)count.ExecuteScalar()!);

        using var write = reader.CreateCommand();
        write.CommandText = "DELETE FROM sessions;";
        Assert.ThrowsAny<Microsoft.Data.Sqlite.SqliteException>(() => write.ExecuteNonQuery());
    }

    [Fact]
    public void Read_only_open_of_a_missing_database_throws_and_creates_nothing()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ScreenTime.Tests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(folder);

        Assert.Throws<FileNotFoundException>(() => Db.OpenReadOnly(paths));
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void Idle_threshold_defaults_to_five_minutes()
    {
        using var db = new TestDb();
        Assert.Equal(300, new SettingsStore(db.Connection).GetIdleThresholdSeconds());
    }

    [Theory]
    [InlineData(10, 60)]
    [InlineData(60, 60)]
    [InlineData(420, 420)]
    [InlineData(1800, 1800)]
    [InlineData(5000, 1800)]
    public void Idle_threshold_is_clamped_to_one_to_thirty_minutes(int stored, int expected)
    {
        using var db = new TestDb();
        var settings = new SettingsStore(db.Connection);

        settings.SetIdleThresholdSeconds(stored);

        Assert.Equal(expected, settings.GetIdleThresholdSeconds());
    }

    [Fact]
    public void Unreadable_or_out_of_range_stored_threshold_falls_back_safely()
    {
        using var db = new TestDb();
        var settings = new SettingsStore(db.Connection);

        settings.Set(SettingsStore.IdleThresholdKey, "not a number");
        Assert.Equal(300, settings.GetIdleThresholdSeconds());

        settings.Set(SettingsStore.IdleThresholdKey, "5");
        Assert.Equal(60, settings.GetIdleThresholdSeconds());
    }

    private static (long Start, long End) ReadSession(TestDb db, long id)
    {
        using var command = db.Connection.CreateCommand();
        command.CommandText = "SELECT start_utc, end_utc FROM sessions WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.GetInt64(0), reader.GetInt64(1));
    }
}

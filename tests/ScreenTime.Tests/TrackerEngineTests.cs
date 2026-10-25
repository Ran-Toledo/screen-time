using ScreenTime.Core;
using ScreenTime.Tracker;

namespace ScreenTime.Tests;

/// <summary>The scenarios from IMPLEMENTATION_PLAN Phase 2. Times are offsets from <see cref="Sim.T0"/>.</summary>
public class TrackerEngineTests
{
    private static readonly ForegroundApp A = new("appa", "App A", null);
    private static readonly ForegroundApp B = new("appb", "App B", null);

    [Fact] // 1 — R6, R12
    public void Ten_seconds_on_one_app_is_one_ten_second_session()
    {
        var sim = new Sim();
        sim.Active(A, 10);
        sim.Stop();

        var s = Assert.Single(sim.Sessions);
        Assert.Equal(("appa", 0L, 10L), sim.Rel(s));
    }

    [Fact] // 2 — R1, R12
    public void Switching_apps_gives_back_to_back_sessions_that_add_up()
    {
        var sim = new Sim();
        sim.Active(A, 10);
        sim.Active(B, 5);
        sim.Active(A, 5);
        sim.Stop();

        Assert.Equal(
            [("appa", 0L, 10L), ("appb", 10L, 15L), ("appa", 15L, 20L)],
            sim.Sessions.Select(sim.Rel));
        Assert.Equal(20, sim.Sessions.Sum(s => s.End - s.Start));
    }

    [Fact] // 3 — R4
    public void Going_idle_ends_the_session_at_the_last_input()
    {
        var sim = new Sim();
        sim.Active(A, 10);          // last input at 9
        sim.Away(A, 400);

        var s = Assert.Single(sim.Sessions);
        Assert.Equal(("appa", 0L, 9L), sim.Rel(s));
        Assert.Null(sim.Engine.CurrentProcessName);
    }

    [Fact] // 4 — R4
    public void Input_after_idle_starts_a_new_session()
    {
        var sim = new Sim();
        sim.Active(A, 10);
        sim.Away(A, 400);           // seconds 10..409
        sim.Active(A, 5);           // back at 410
        sim.Stop();

        Assert.Equal([("appa", 0L, 9L), ("appa", 410L, 415L)], sim.Sessions.Select(sim.Rel));
    }

    [Fact] // 5 — R4, D6
    public void A_changed_threshold_applies_from_the_next_tick()
    {
        var sim = new Sim();
        sim.Active(A, 10);
        sim.Engine.IdleThresholdSeconds = 60;
        sim.Away(A, 59);            // idle for 50 s at most: still counting
        Assert.Equal("appa", sim.Engine.CurrentProcessName);

        sim.Away(A, 1);             // tick at 69: 60 s since the last input
        Assert.Null(sim.Engine.CurrentProcessName);
        Assert.Equal(("appa", 0L, 9L), sim.Rel(Assert.Single(sim.Sessions)));
    }

    [Fact] // 6 — R5
    public void No_foreground_window_keeps_the_current_app()
    {
        var sim = new Sim();
        sim.Active(A, 5);
        sim.Active(null, 3);
        sim.Active(A, 5);
        sim.Stop();

        Assert.Equal(("appa", 0L, 13L), sim.Rel(Assert.Single(sim.Sessions)));
    }

    [Fact] // 7 — R5
    public void No_foreground_window_with_nothing_open_records_nothing()
    {
        var sim = new Sim();
        sim.Active(null, 5);
        sim.Stop();

        Assert.Empty(sim.Sessions);
    }

    [Fact] // 8 — R3
    public void Locking_stops_the_clock_until_unlock()
    {
        var sim = new Sim();
        sim.Active(A, 10);
        sim.Engine.SetLocked(true, sim.Now);
        sim.Active(A, 60);          // ticks (even with input) while locked
        sim.Engine.SetLocked(false, sim.Now);
        sim.Active(A, 5);
        sim.Stop();

        Assert.Equal([("appa", 0L, 10L), ("appa", 70L, 75L)], sim.Sessions.Select(sim.Rel));
    }

    [Fact] // 9 — R2
    public void Pausing_records_nothing_until_resumed()
    {
        var sim = new Sim();
        sim.Active(A, 10);
        sim.Engine.Paused = true;
        sim.Active(A, 30);
        sim.Engine.Paused = false;
        sim.Active(A, 5);
        sim.Stop();

        Assert.Equal([("appa", 0L, 10L), ("appa", 40L, 45L)], sim.Sessions.Select(sim.Rel));
    }

    [Fact] // 10 — R14
    public void A_long_gap_between_ticks_ends_the_session_at_the_last_good_tick()
    {
        var sim = new Sim();
        sim.Active(A, 10);          // last tick at 9
        sim.Now += 120;             // e.g. slept without a suspend event
        sim.Active(A, 5);
        sim.Stop();

        Assert.Equal([("appa", 0L, 9L), ("appa", 130L, 135L)], sim.Sessions.Select(sim.Rel));
    }

    [Fact] // 10b — R14
    public void A_gap_at_the_limit_is_not_treated_as_sleep()
    {
        var sim = new Sim();
        sim.Active(A, 10);
        sim.Now += TrackerEngine.MaxTickGapSeconds - 1; // next tick exactly 5 s after the last
        sim.Active(A, 5);
        sim.Stop();

        Assert.Single(sim.Sessions);
    }

    [Fact] // 11 — R14
    public void The_clock_going_backwards_ends_the_session_and_starts_a_new_one()
    {
        var sim = new Sim();
        sim.Active(A, 10);
        sim.Now -= 3600;
        sim.Active(A, 5);
        sim.Stop();

        Assert.Equal([("appa", 0L, 9L), ("appa", 10L - 3600, 15L - 3600)], sim.Sessions.Select(sim.Rel));
    }

    [Fact] // 12 — R15
    public void An_open_session_is_flushed_every_60_seconds()
    {
        var sim = new Sim();
        sim.Active(A, 150);

        var s = Assert.Single(sim.Sessions);
        Assert.Equal([60L, 120L], sim.Sink.Updates.Where(u => u.Id == s.Id).Select(u => u.End - Sim.T0));
        Assert.Equal(120, s.End - Sim.T0);
    }

    [Fact] // 13 — R16
    public void Stop_closes_the_session_and_is_safe_to_repeat()
    {
        var sim = new Sim();
        sim.Active(A, 30);
        sim.Stop();
        var updates = sim.Sink.Updates.Count;
        sim.Stop();

        Assert.Equal(("appa", 0L, 30L), sim.Rel(Assert.Single(sim.Sessions)));
        Assert.Equal(updates, sim.Sink.Updates.Count);
    }

    [Fact] // 14 — R4
    public void Idle_end_is_never_before_the_session_start()
    {
        var sim = new Sim();
        sim.Active(A, 10);          // last input at 9
        sim.Away(B, 400);           // B takes focus at 10 with no input, then idle

        Assert.Equal([("appa", 0L, 10L), ("appb", 10L, 10L)], sim.Sessions.Select(sim.Rel));
    }

    [Fact] // 15 — R3
    public void Resuming_from_sleep_onto_the_lock_screen_counts_nothing_until_unlock()
    {
        var sim = new Sim();
        sim.Active(A, 10);
        sim.Engine.SetLocked(true, sim.Now);
        sim.Engine.SetSuspended(true, sim.Now);
        sim.Now += 600;
        sim.Engine.SetSuspended(false, sim.Now);
        sim.Active(A, 20);          // lock screen showing: nothing counts
        sim.Engine.SetLocked(false, sim.Now);
        sim.Active(A, 5);
        sim.Stop();

        Assert.Equal([("appa", 0L, 10L), ("appa", 630L, 635L)], sim.Sessions.Select(sim.Rel));
    }

    [Fact] // R7
    public void Process_names_differing_only_in_case_are_the_same_app()
    {
        var sim = new Sim();
        sim.Active(new ForegroundApp("Chrome", "Google Chrome", null), 5);
        sim.Active(new ForegroundApp("chrome", "Google Chrome", null), 5);
        sim.Stop();

        Assert.Single(sim.Sessions);
    }

    [Fact] // N6
    public void A_failed_write_is_retried_on_the_next_tick()
    {
        var sim = new Sim();
        sim.Sink.FailNextOpen = true;
        Assert.ThrowsAny<Exception>(() => sim.Active(A, 1)); // fails before the clock advances
        sim.Active(A, 5);
        sim.Stop();

        Assert.Equal(("appa", 0L, 5L), sim.Rel(Assert.Single(sim.Sessions)));
    }

    /// <summary>Drives the engine one tick per simulated second.</summary>
    private sealed class Sim
    {
        public const long T0 = 1_800_000_000;

        public Sim() => Engine = new TrackerEngine(Sink);

        public FakeSink Sink { get; } = new();
        public TrackerEngine Engine { get; }
        public long Now { get; set; } = T0;
        public long LastInput { get; private set; } = T0;

        public IReadOnlyList<FakeSink.Session> Sessions => Sink.Sessions;

        /// <summary>The user is present and using <paramref name="app"/> for this many seconds.</summary>
        public void Active(ForegroundApp? app, int seconds)
        {
            for (var i = 0; i < seconds; i++)
            {
                LastInput = Now;
                Engine.Tick(Now, LastInput, app);
                Now++;
            }
        }

        /// <summary>No input for this many seconds while <paramref name="app"/> is in front.</summary>
        public void Away(ForegroundApp? app, int seconds)
        {
            for (var i = 0; i < seconds; i++)
            {
                Engine.Tick(Now, LastInput, app);
                Now++;
            }
        }

        public void Stop() => Engine.Stop(Now);

        public (string App, long Start, long End) Rel(FakeSink.Session s) =>
            (s.App.ProcessName, s.Start - T0, s.End - T0);
    }

    private sealed class FakeSink : ISessionSink
    {
        public sealed record Session(long Id, ForegroundApp App, long Start)
        {
            public long End { get; set; } = Start;
        }

        public List<Session> Sessions { get; } = [];
        public List<(long Id, long End)> Updates { get; } = [];
        public bool FailNextOpen { get; set; }

        public long Open(ForegroundApp app, long startUtc)
        {
            if (FailNextOpen)
            {
                FailNextOpen = false;
                throw new InvalidOperationException("simulated database failure");
            }
            var session = new Session(Sessions.Count + 1, app, startUtc);
            Sessions.Add(session);
            return session.Id;
        }

        public void UpdateEnd(long sessionId, long endUtc)
        {
            Sessions.Single(s => s.Id == sessionId).End = endUtc;
            Updates.Add((sessionId, endUtc));
        }
    }
}

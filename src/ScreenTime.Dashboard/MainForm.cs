using ScottPlot.TickGenerators;
using ScottPlot.WinForms;
using ScreenTime.Core;

namespace ScreenTime.Dashboard;

/// <summary>The dashboard window (DESIGN §5.1): one day at a time, plus the 7 days ending on it.</summary>
internal sealed class MainForm : Form
{
    private const int TopAppCount = 10;
    private const int WeekDays = 7;
    private const int AppLabelMaxLength = 28;

    private static readonly ScottPlot.Color Accent = ScottPlot.Color.FromHex("#2B6CB0");
    private static readonly ScottPlot.Color Muted = ScottPlot.Color.FromHex("#A9C3E0");

    private readonly AppPaths _paths;
    private readonly System.ComponentModel.Container _components = new();
    private readonly DateTimePicker _datePicker;
    private readonly Button _nextButton;
    private readonly Label _totalLabel;
    private readonly Label _emptyLabel;
    private readonly Label _statusLabel;
    private readonly ListView _appList;
    private readonly FormsPlot _appsPlot;
    private readonly FormsPlot _hoursPlot;
    private readonly FormsPlot _daysPlot;
    private DateOnly _today = Today;

    public MainForm(AppPaths paths)
    {
        _paths = paths;

        // Sizes below are in 96-DPI pixels; ResumeLayout scales them to the display.
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        Text = "ScreenTime";
        Size = new Size(1100, 720);
        MinimumSize = new Size(820, 560);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);

        // Toolbar (D2, D5, D6)
        var previousButton = new Button { Text = "◀", Width = 36, Height = 28 };
        previousButton.Click += (_, _) => SelectDay(SelectedDay.AddDays(-1));

        _datePicker = new DateTimePicker
        {
            Format = DateTimePickerFormat.Custom,
            CustomFormat = "ddd d MMM yyyy",
            Width = 160,
            MaxDate = _today.ToDateTime(TimeOnly.MinValue),
            Value = _today.ToDateTime(TimeOnly.MinValue),
            Margin = new Padding(3, 5, 3, 3),
        };
        _datePicker.ValueChanged += (_, _) => Reload();

        _nextButton = new Button { Text = "▶", Width = 36, Height = 28 };
        _nextButton.Click += (_, _) => SelectDay(SelectedDay.AddDays(1));

        var todayButton = new Button { Text = "Today", AutoSize = true, Height = 28 };
        todayButton.Click += (_, _) => SelectDay(Today);

        var refreshButton = new Button { Text = "Refresh", AutoSize = true, Height = 28 };
        refreshButton.Click += (_, _) => Reload();

        var settingsButton = new Button { Text = "Settings…", AutoSize = true, Height = 28 };
        settingsButton.Click += (_, _) => OpenSettings();

        var navigation = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Left };
        navigation.Controls.AddRange([previousButton, _datePicker, _nextButton, todayButton]);

        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Right };
        actions.Controls.AddRange([refreshButton, settingsButton]);

        var toolbar = new Panel { Dock = DockStyle.Fill, Height = 36 };
        toolbar.Controls.Add(navigation);
        toolbar.Controls.Add(actions);

        // Total (D3a)
        _totalLabel = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 16f),
            Margin = new Padding(6, 8, 6, 8),
        };

        // Per-app list (D3b) with an empty state (D7)
        _appList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            MultiSelect = false,
        };
        _appList.Columns.Add("App");
        _appList.Columns.Add("Time", -2, HorizontalAlignment.Right);
        _appList.Columns.Add("Share", -2, HorizontalAlignment.Right);
        _appList.Resize += (_, _) => SizeAppColumns();

        _emptyLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = "No screen time recorded for this day.",
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = SystemColors.GrayText,
            Visible = false,
        };

        // Charts (D3c, D3d, D4)
        _appsPlot = CreatePlot();
        _hoursPlot = CreatePlot();
        _daysPlot = CreatePlot();

        var charts = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        charts.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        charts.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        charts.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        charts.Controls.Add(_appsPlot, 0, 0);
        charts.Controls.Add(_hoursPlot, 0, 1);
        charts.Controls.Add(_daysPlot, 0, 2);

        var listPanel = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0) };
        listPanel.Controls.Add(_appList);
        listPanel.Controls.Add(_emptyLabel);

        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 66));
        content.Controls.Add(listPanel, 0, 0);
        content.Controls.Add(charts, 1, 0);

        _statusLabel = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(6, 4, 6, 4) };

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(8) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(toolbar, 0, 0);
        root.Controls.Add(_totalLabel, 0, 1);
        root.Controls.Add(content, 0, 2);
        root.Controls.Add(_statusLabel, 0, 3);
        Controls.Add(root);

        // D5: keep today's view current
        var refreshTimer = new System.Windows.Forms.Timer(_components) { Interval = 60_000 };
        refreshTimer.Tick += (_, _) => OnRefreshTimer();
        refreshTimer.Start();

        ResumeLayout(false);
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    private DateOnly SelectedDay => DateOnly.FromDateTime(_datePicker.Value);

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Reload();
    }

    /// <summary>Time and Share get fixed widths; App takes the rest, so nothing scrolls sideways.</summary>
    private void SizeAppColumns()
    {
        var time = LogicalToDeviceUnits(70);
        var share = LogicalToDeviceUnits(56);
        var scrollbar = SystemInformation.VerticalScrollBarWidth;
        _appList.Columns[1].Width = time;
        _appList.Columns[2].Width = share;
        _appList.Columns[0].Width = Math.Max(LogicalToDeviceUnits(80), _appList.ClientSize.Width - time - share - scrollbar);
    }

    private void SelectDay(DateOnly day)
    {
        if (day > _today)
            day = _today;
        if (day == SelectedDay)
            Reload();
        else
            _datePicker.Value = day.ToDateTime(TimeOnly.MinValue); // ValueChanged reloads
    }

    private void OnRefreshTimer()
    {
        var today = Today;
        if (today != _today)
        {
            // Past midnight: allow the new day, and follow it if we were showing "today".
            var wasShowingToday = SelectedDay == _today;
            _today = today;
            _datePicker.MaxDate = today.ToDateTime(TimeOnly.MinValue);
            if (wasShowingToday)
            {
                SelectDay(today);
                return;
            }
        }

        if (SelectedDay == _today)
            Reload();
        else
            _nextButton.Enabled = true;
    }

    private void Reload()
    {
        var day = SelectedDay;
        _nextButton.Enabled = day < _today;

        try
        {
            var (summary, week) = LoadDay(day);
            ShowTotal(summary);
            ShowAppList(summary);
            DrawApps(summary);
            DrawHours(summary);
            DrawDays(week);
            _statusLabel.Text = $"Updated {DateTime.Now:t}";
        }
        catch (Exception e)
        {
            _statusLabel.Text = $"Couldn't read screen time data: {e.Message}";
        }
    }

    /// <summary>
    /// Reads one day and its week through a short-lived read-only connection, so the dashboard
    /// never holds the database open and can never write to it. Before the tracker has created
    /// the database, every day is simply empty (D7).
    /// </summary>
    private (DaySummary Summary, IReadOnlyList<DailyTotal> Week) LoadDay(DateOnly day)
    {
        if (!File.Exists(_paths.DatabasePath))
        {
            var emptyDay = new DaySummary(day, TimeSpan.Zero, [], Enumerable.Repeat(TimeSpan.Zero, 24).ToList());
            var emptyWeek = Enumerable.Range(0, WeekDays)
                .Select(i => new DailyTotal(day.AddDays(i - (WeekDays - 1)), TimeSpan.Zero))
                .ToList();
            return (emptyDay, emptyWeek);
        }

        using var db = Db.OpenReadOnly(_paths);
        var stats = new Stats(db);
        return (stats.GetDay(day), stats.GetDailyTotals(day, WeekDays));
    }

    private void ShowTotal(DaySummary summary)
    {
        var when = summary.Day == _today ? "today" : $"on {summary.Day.ToDateTime(TimeOnly.MinValue):dddd d MMMM}";
        _totalLabel.Text = $"Screen time {when}: {DurationFormat.Short(summary.Total)}";
    }

    private void ShowAppList(DaySummary summary)
    {
        _appList.BeginUpdate();
        _appList.Items.Clear();
        foreach (var app in summary.Apps)
        {
            var item = new ListViewItem(app.DisplayName) { ToolTipText = app.ProcessName };
            item.SubItems.Add(DurationFormat.Short(app.Duration));
            item.SubItems.Add($"{app.Share:P0}");
            _appList.Items.Add(item);
        }
        _appList.EndUpdate();

        var empty = summary.Apps.Count == 0;
        _appList.Visible = !empty;
        _emptyLabel.Visible = empty;
    }

    private void DrawApps(DaySummary summary)
    {
        var plot = _appsPlot.Plot;
        plot.Clear();

        var top = summary.Apps.Take(TopAppCount).ToList();
        var slots = Math.Max(top.Count, 1);

        // Largest at the top: the first app gets the highest position.
        var bars = top.Select((app, i) => Bar(slots - 1 - i, app.Duration.TotalHours, Accent)).ToList();
        if (bars.Count > 0)
            plot.Add.Bars(bars).Horizontal = true;

        plot.Axes.Left.TickGenerator = new NumericManual(
            top.Select((_, i) => (double)(slots - 1 - i)).ToArray(),
            top.Select(a => Shorten(a.DisplayName)).ToArray());

        var max = top.Count > 0 ? top[0].Duration.TotalHours : 0;
        plot.Axes.SetLimitsX(0, NiceMax(max));
        plot.Axes.SetLimitsY(-0.6, slots - 0.4);
        plot.Title("Top apps");
        plot.XLabel("Hours");
        _appsPlot.Refresh();
    }

    private void DrawHours(DaySummary summary)
    {
        var plot = _hoursPlot.Plot;
        plot.Clear();

        var bars = summary.Hours.Select((h, i) => Bar(i, h.TotalMinutes, Accent)).Where(b => b.Value > 0).ToList();
        if (bars.Count > 0)
            plot.Add.Bars(bars);

        var ticks = Enumerable.Range(0, 8).Select(i => i * 3).ToArray();
        plot.Axes.Bottom.TickGenerator = new NumericManual(
            ticks.Select(t => (double)t).ToArray(),
            ticks.Select(t => $"{t:00}:00").ToArray());

        plot.Axes.SetLimitsX(-0.6, 23.6);
        plot.Axes.SetLimitsY(0, 62);
        plot.Title("By hour");
        plot.YLabel("Minutes");
        _hoursPlot.Refresh();
    }

    private void DrawDays(IReadOnlyList<DailyTotal> week)
    {
        var plot = _daysPlot.Plot;
        plot.Clear();

        // The selected day is the last one and is highlighted.
        var bars = week.Select((d, i) => Bar(i, d.Total.TotalHours, i == week.Count - 1 ? Accent : Muted)).ToList();
        plot.Add.Bars(bars);

        plot.Axes.Bottom.TickGenerator = new NumericManual(
            week.Select((_, i) => (double)i).ToArray(),
            week.Select(d => d.Day.ToDateTime(TimeOnly.MinValue).ToString("ddd d")).ToArray());

        plot.Axes.SetLimitsX(-0.6, week.Count - 0.4);
        plot.Axes.SetLimitsY(0, NiceMax(week.Max(d => d.Total.TotalHours)));
        plot.Title($"Last {WeekDays} days");
        plot.YLabel("Hours");
        _daysPlot.Refresh();
    }

    private void OpenSettings()
    {
        using var settings = new SettingsForm(_paths);
        settings.ShowDialog(this);
    }

    private static FormsPlot CreatePlot()
    {
        var formsPlot = new FormsPlot { Dock = DockStyle.Fill };
        formsPlot.UserInputProcessor.Disable(); // static charts: no pan/zoom
        return formsPlot;
    }

    private static ScottPlot.Bar Bar(double position, double value, ScottPlot.Color color) => new()
    {
        Position = position,
        Value = value,
        FillColor = color,
        LineWidth = 0,
        Size = 0.7,
    };

    /// <summary>A little headroom above the tallest bar; at least 1 so empty charts still have an axis.</summary>
    private static double NiceMax(double max) => Math.Max(1, max * 1.15);

    private static string Shorten(string name) =>
        name.Length <= AppLabelMaxLength ? name : name[..(AppLabelMaxLength - 1)] + "…";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _components.Dispose();
        base.Dispose(disposing);
    }
}

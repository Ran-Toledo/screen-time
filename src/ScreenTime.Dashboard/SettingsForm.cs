using ScreenTime.Core;

namespace ScreenTime.Dashboard;

/// <summary>Edits the idle threshold (D6). The tracker picks it up within 60 seconds.</summary>
internal sealed class SettingsForm : Form
{
    private readonly AppPaths _paths;
    private readonly NumericUpDown _idleMinutes;

    public SettingsForm(AppPaths paths)
    {
        _paths = paths;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        Text = "Settings";
        Font = new Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        _idleMinutes = new NumericUpDown
        {
            Minimum = SettingsStore.MinIdleThresholdSeconds / 60,
            Maximum = SettingsStore.MaxIdleThresholdSeconds / 60,
            Width = 60,
            Value = SettingsStore.DefaultIdleThresholdSeconds / 60,
        };

        var idleRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        idleRow.Controls.Add(new Label { Text = "Count me as idle after", AutoSize = true, Margin = new Padding(3, 6, 3, 3) });
        idleRow.Controls.Add(_idleMinutes);
        idleRow.Controls.Add(new Label { Text = "minutes without input", AutoSize = true, Margin = new Padding(3, 6, 3, 3) });

        var note = new Label
        {
            Text = "The tracker applies changes within a minute.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 3, 3, 12),
        };

        var ok = new Button { Text = "OK", DialogResult = DialogResult.None, AutoSize = true };
        ok.Click += (_, _) => Save();
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        AcceptButton = ok;
        CancelButton = cancel;

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        layout.Controls.Add(idleRow);
        layout.Controls.Add(note);
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        ResumeLayout(false);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        try
        {
            using var db = Db.Open(_paths);
            var minutes = new SettingsStore(db).GetIdleThresholdSeconds() / 60;
            _idleMinutes.Value = Math.Clamp(minutes, (int)_idleMinutes.Minimum, (int)_idleMinutes.Maximum);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't read settings: {ex.Message}", "ScreenTime", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void Save()
    {
        try
        {
            using var db = Db.Open(_paths);
            new SettingsStore(db).SetIdleThresholdSeconds((int)_idleMinutes.Value * 60);
            DialogResult = DialogResult.OK;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't save settings: {ex.Message}", "ScreenTime", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}

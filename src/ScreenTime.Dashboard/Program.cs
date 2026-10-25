namespace ScreenTime.Dashboard;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        // MainForm is added in Phase 4.
        Application.Run(new Form { Text = "ScreenTime" });
    }
}

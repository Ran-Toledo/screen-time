namespace ScreenTime.Tracker;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        // Tray context and tracking loop are added in Phase 3.
    }
}

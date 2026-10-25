using ScreenTime.Core;

namespace ScreenTime.Dashboard;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(new AppPaths()));
    }
}

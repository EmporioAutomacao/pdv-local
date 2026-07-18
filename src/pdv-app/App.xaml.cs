using System.Windows;

namespace PdvLocal.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var prefs = UserPreferences.Load();
        ThemeManager.Apply(prefs.Theme);

        var config = PdvAppConfiguration.Load();
        var loginWindow = new LoginWindow(config.PdvLocalConnectionString);
        if (loginWindow.ShowDialog() == true && loginWindow.AuthenticatedOperator is { } op)
        {
            var mainWindow = new MainWindow(op);
            mainWindow.Show();
        }
        else
        {
            Shutdown();
        }
    }
}

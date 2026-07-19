using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace PdvLocal.App;

public partial class App : Application
{
    private static readonly string CrashLogPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "PDVLocal", "pdvapp-crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

        // Evita o auto-encerramento (OnLastWindowClose) quando o diálogo de
        // login modal fecha antes de a MainWindow ser exibida.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try
        {
            var prefs = UserPreferences.Load();
            ThemeManager.Apply(prefs.Theme);

            var config = PdvAppConfiguration.Load();
            var loginWindow = new LoginWindow(config.PdvLocalConnectionString);
            if (loginWindow.ShowDialog() == true && loginWindow.AuthenticatedOperator is { } op)
            {
                var mainWindow = new MainWindow(op);
                MainWindow = mainWindow;
                ShutdownMode = ShutdownMode.OnMainWindowClose;
                mainWindow.Show();
            }
            else
            {
                Shutdown();
            }
        }
        catch (Exception ex)
        {
            LogCrash("OnStartup", ex);
            throw;
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash("DispatcherUnhandledException", e.Exception);
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        LogCrash("AppDomainUnhandledException", e.ExceptionObject as Exception);
    }

    private static void LogCrash(string origin, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            var entry = $"[{DateTimeOffset.Now:O}] {origin}{Environment.NewLine}{ex}{Environment.NewLine}{new string('-', 60)}{Environment.NewLine}";
            File.AppendAllText(CrashLogPath, entry);
        }
        catch
        {
            // best-effort — nao mascarar o crash original
        }
    }
}

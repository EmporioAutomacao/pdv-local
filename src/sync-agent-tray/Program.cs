using SyncAgent.Tray.LocalApi;

namespace SyncAgent.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri("http://127.0.0.1:47891")
        };

        Application.Run(new TrayApplicationContext(new LocalStatusClient(httpClient)));
    }
}

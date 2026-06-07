using System.Windows.Forms;

namespace SyncAgent.Installer;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new InstallerWizardForm());
    }
}

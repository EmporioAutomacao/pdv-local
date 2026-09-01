using System.ServiceProcess;
using System.Windows.Forms;

namespace SyncAgent.Installer;

internal static class Program
{
    private const string ServiceName = "AraraSuiteSync";

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        string? payloadRoot;
        using (var progress = new ExtractionProgressForm())
        {
            Application.Run(progress);
            payloadRoot = progress.PayloadRoot;
        }

        if (payloadRoot is not null && ExistingInstallationDetected())
        {
            Application.Run(new UpdateWizardForm(payloadRoot));
            return;
        }

        Application.Run(new InstallerWizardForm(payloadRoot));
    }

    private static bool ExistingInstallationDetected()
    {
        try
        {
            return ServiceController.GetServices().Any(service =>
                string.Equals(service.ServiceName, ServiceName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }
}

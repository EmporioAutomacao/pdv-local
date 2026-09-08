using System.Diagnostics;
using System.Security.Principal;
using System.ServiceProcess;
using System.Windows.Forms;

namespace SyncAgent.Installer;

internal static class Program
{
    private const string ServiceName = "AraraSuiteSync";

    [STAThread]
    private static int Main(string[] args)
    {
        var auto = args.Any(a =>
            a.Equals("--auto", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("/auto", StringComparison.OrdinalIgnoreCase));

        if (auto)
        {
            // Chamado pela bandeja (usuario) ou por script de deploy/GPO: sem
            // wizard. Se nao estiver elevado, relanca a si mesmo com UAC.
            if (!IsElevated())
            {
                return RelaunchElevatedAuto();
            }

            return AutoUpdateRunner.Run();
        }

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
            return 0;
        }

        Application.Run(new InstallerWizardForm(payloadRoot));
        return 0;
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static int RelaunchElevatedAuto()
    {
        try
        {
            var exePath = Environment.ProcessPath ?? Application.ExecutablePath;
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--auto",
                UseShellExecute = true,
                Verb = "runas",
            };
            using var elevated = Process.Start(psi);
            if (elevated is null)
            {
                return 1;
            }

            elevated.WaitForExit();
            return elevated.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Usuario recusou o UAC.
            return 1223;
        }
        catch
        {
            return 1;
        }
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

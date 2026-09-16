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
                return RelaunchElevated("--auto");
            }

            return AutoUpdateRunner.Run();
        }

        // Duplo clique manual no .exe (instalacao/atualizacao interativa): sem
        // isso o processo roda sem privilegio nenhum ate o usuario lembrar de
        // "Executar como Administrador", e falha mais adiante (ex.: initdb.exe
        // sem permissao pra criar a pasta de dados do PostgreSQL) sem deixar
        // claro o motivo. Um <ApplicationManifest> com requireAdministrator
        // foi tentado no lugar disso e quebrou o build de arquivo unico
        // (PublishSingleFile + self-contained) com "configuracao lado a lado
        // incorreta" - relancar a si mesmo com "runas" e o mesmo mecanismo ja
        // usado e testado no fluxo --auto acima.
        if (!IsElevated())
        {
            return RelaunchElevated(null);
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

    private static int RelaunchElevated(string? arguments)
    {
        try
        {
            var exePath = Environment.ProcessPath ?? Application.ExecutablePath;
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas",
            };
            if (!string.IsNullOrWhiteSpace(arguments))
            {
                psi.Arguments = arguments;
            }
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

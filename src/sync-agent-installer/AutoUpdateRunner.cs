using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Win32;

namespace SyncAgent.Installer;

/// <summary>
/// Modo <c>--auto</c> do instalador: atualiza uma instalacao existente sem
/// wizard nenhum (uma janela de progresso so, ou nem isso quando chamado pelo
/// proprio SyncAgent). Reaproveita o payload embutido no .exe e roda o
/// <c>self-update.ps1</c> que veio no pacote (a versao mais nova do script) de
/// um processo proprio - nao de dentro do servico. Feito para rollout em massa
/// (GPO / script de deploy) e para o botao "Atualizar App" da bandeja.
/// </summary>
internal static class AutoUpdateRunner
{
    private const string ServiceName = "AraraSuiteSync";
    private const string DefaultInstallRoot = @"C:\Program Files\AraraSuite.com.br";
    private const string LogSource = "AraraSuite Sync Update";

    public static int Run()
    {
        try
        {
            Log("Auto-update: extraindo pacote embutido...");
            var payloadRoot = EmbeddedPayload.ExtractIfPresent();
            if (payloadRoot is null)
            {
                Log("ERRO: este .exe nao tem payload embutido (build de desenvolvimento?).");
                return 3;
            }

            var installRoot = ResolveInstallRoot();
            var installed = ReadVersionFile(Path.Combine(installRoot, "Sync", "Agent", "VERSION"));
            var package = ReadVersionFile(Path.Combine(payloadRoot, "payload", "Sync", "Agent", "VERSION"));
            Log($"Instalado: {installed ?? "?"}  ->  Pacote: {package ?? "?"}  (InstallRoot: {installRoot})");

            if (!ExistingInstallationDetected())
            {
                Log("ERRO: nenhuma instalacao AraraSuite (servico 'AraraSuiteSync') detectada nesta maquina.");
                return 4;
            }

            if (string.Equals(installed, package, StringComparison.OrdinalIgnoreCase))
            {
                Log("Ja esta na versao deste pacote. Nada a fazer.");
                return 0;
            }

            var zipPath = Path.Combine(Path.GetTempPath(), $"araras-autoupdate-{Guid.NewGuid():N}.zip");
            Log("Re-empacotando payload...");
            ZipFile.CreateFromDirectory(payloadRoot, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false);

            var scriptPath = Path.Combine(payloadRoot, "payload", "Sync", "Agent", "self-update.ps1");
            if (!File.Exists(scriptPath))
            {
                Log($"ERRO: self-update.ps1 nao encontrado em {scriptPath}.");
                return 5;
            }

            var args = "-NoProfile -ExecutionPolicy Bypass -File " + Quote(scriptPath)
                + " -ZipPath " + Quote(zipPath)
                + " -Version " + Quote(package ?? "unknown")
                + " -InstallRoot " + Quote(installRoot)
                + " -ServiceName " + Quote(ServiceName);

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) Log(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log("! " + e.Data); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();

            Log($"self-update.ps1 terminou com codigo {process.ExitCode}.");
            try { File.Delete(zipPath); } catch { /* melhor esforco */ }
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Log("ERRO no auto-update: " + ex);
            return 1;
        }
    }

    private static void Log(string message)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
        try
        {
            if (!EventLog.SourceExists(LogSource))
            {
                EventLog.CreateEventSource(LogSource, "Application");
            }

            var type = message.StartsWith("ERRO", StringComparison.Ordinal) || message.StartsWith("! ", StringComparison.Ordinal)
                ? EventLogEntryType.Error
                : EventLogEntryType.Information;
            EventLog.WriteEntry(LogSource, message, type, 5001);
        }
        catch
        {
            // Event Log e conveniencia; nao pode derrubar o update.
        }
    }

    private static bool ExistingInstallationDetected()
    {
        try
        {
            return System.ServiceProcess.ServiceController.GetServices()
                .Any(s => string.Equals(s.ServiceName, ServiceName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveInstallRoot()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
            if (key?.GetValue("ImagePath") is string imagePath && !string.IsNullOrWhiteSpace(imagePath))
            {
                var exePath = imagePath.Trim('"');
                // <InstallRoot>\Sync\Agent\SyncAgent.exe -> <InstallRoot>
                var agentDir = Path.GetDirectoryName(exePath);
                var syncDir = agentDir is null ? null : Path.GetDirectoryName(agentDir);
                var root = syncDir is null ? null : Path.GetDirectoryName(syncDir);
                if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                {
                    return root!;
                }
            }
        }
        catch
        {
            // cai para o default
        }

        return DefaultInstallRoot;
    }

    private static string? ReadVersionFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "`\"") + "\"";
}

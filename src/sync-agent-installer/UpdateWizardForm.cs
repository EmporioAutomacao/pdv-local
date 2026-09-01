using System.Diagnostics;
using System.IO.Compression;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace SyncAgent.Installer;

/// <summary>
/// Modo "Atualizar" do instalador: mostrado quando o Program.cs detecta uma
/// instalacao AraraSuite ja existente na maquina. Reaproveita o mesmo
/// payload embutido no .exe (o mesmo usado para instalacao nova) mas, em vez
/// de rodar o assistente completo (install-sync-agent.ps1), re-empacota o
/// payload extraido num .zip temporario e invoca diretamente
/// payload\Sync\Agent\self-update.ps1 — o mesmo script que o SyncAgent usa
/// no auto-update, com o mesmo progresso estruturado (update-status.json).
/// </summary>
internal sealed class UpdateWizardForm : Form
{
    private const string ServiceName = "AraraSuiteSync";
    private const string DefaultInstallRoot = @"C:\Program Files\AraraSuite.com.br";

    private readonly string _payloadRoot;
    private readonly string _installRoot;
    private readonly string? _installedVersion;
    private readonly string? _packageVersion;

    private readonly Label _summaryLabel;
    private readonly Label _phaseLabel;
    private readonly ProgressBar _bar;
    private readonly TextBox _logBox;
    private readonly Button _updateButton;
    private readonly Button _closeButton;
    private readonly System.Windows.Forms.Timer _diskPollTimer;

    public UpdateWizardForm(string payloadRoot)
    {
        _payloadRoot = payloadRoot;
        _installRoot = ResolveInstallRoot();
        _installedVersion = ReadVersionFile(Path.Combine(_installRoot, "Sync", "Agent", "VERSION"));
        _packageVersion = ReadVersionFile(Path.Combine(_payloadRoot, "payload", "Sync", "Agent", "VERSION"));

        Text = "AraraSuite - Atualizar";
        Width = 620;
        Height = 480;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        _summaryLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 64,
            Padding = new Padding(16, 12, 16, 0),
            Text = BuildSummaryText()
        };
        _phaseLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 24,
            Padding = new Padding(16, 0, 16, 0),
            Text = ""
        };
        _bar = new ProgressBar
        {
            Dock = DockStyle.Top,
            Height = 20,
            Margin = new Padding(16, 0, 16, 0),
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 0
        };
        _logBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9)
        };

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8)
        };
        _closeButton = new Button { Text = "Fechar", Width = 100 };
        _closeButton.Click += (_, _) => Close();
        _updateButton = new Button { Text = "Atualizar", Width = 120 };
        _updateButton.Click += async (_, _) => await RunUpdateAsync();
        buttonPanel.Controls.Add(_closeButton);
        buttonPanel.Controls.Add(_updateButton);

        if (!VersionsDiffer())
        {
            _updateButton.Enabled = false;
        }

        Controls.Add(_logBox);
        Controls.Add(buttonPanel);
        Controls.Add(_bar);
        Controls.Add(_phaseLabel);
        Controls.Add(_summaryLabel);

        _diskPollTimer = new System.Windows.Forms.Timer { Interval = 700 };
        _diskPollTimer.Tick += (_, _) => PollDiskStatus();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _diskPollTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private bool VersionsDiffer()
    {
        return !string.Equals(_installedVersion, _packageVersion, StringComparison.OrdinalIgnoreCase);
    }

    private string BuildSummaryText()
    {
        var installed = _installedVersion ?? "desconhecida";
        var package = _packageVersion ?? "desconhecida";
        var note = VersionsDiffer()
            ? ""
            : " (ja esta na versao mais recente deste pacote)";
        return $"Instalacao existente detectada em {_installRoot}.\r\n" +
               $"Versao instalada: {installed}   →   Versao deste pacote: {package}{note}";
    }

    private async Task RunUpdateAsync()
    {
        if (!IsAdministrator())
        {
            MessageBox.Show(
                "Execute este atualizador como Administrador.",
                "Atualizar",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        _updateButton.Enabled = false;
        _closeButton.Enabled = false;
        _bar.MarqueeAnimationSpeed = 30;
        AppendLog("Preparando pacote de atualizacao...");

        string zipPath;
        try
        {
            zipPath = Path.Combine(Path.GetTempPath(), $"araras-update-{Guid.NewGuid():N}.zip");
            await Task.Run(() => ZipFile.CreateFromDirectory(_payloadRoot, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false));
        }
        catch (Exception ex)
        {
            AppendLog($"Falha ao preparar o pacote: {ex.Message}");
            _updateButton.Enabled = true;
            _closeButton.Enabled = true;
            return;
        }

        var scriptPath = Path.Combine(_payloadRoot, "payload", "Sync", "Agent", "self-update.ps1");
        if (!File.Exists(scriptPath))
        {
            AppendLog($"self-update.ps1 nao encontrado em {scriptPath}.");
            _updateButton.Enabled = true;
            _closeButton.Enabled = true;
            return;
        }

        var args = "-NoProfile -ExecutionPolicy Bypass -File " + Quote(scriptPath) +
                   " -ZipPath " + Quote(zipPath) +
                   " -Version " + Quote(_packageVersion ?? "unknown") +
                   " -InstallRoot " + Quote(_installRoot) +
                   " -ServiceName " + Quote(ServiceName);

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => AppendLog(e.Data);
        process.ErrorDataReceived += (_, e) => AppendLog(e.Data);

        _diskPollTimer.Start();
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();
        _diskPollTimer.Stop();
        PollDiskStatus();

        _bar.MarqueeAnimationSpeed = 0;
        _bar.Style = ProgressBarStyle.Continuous;
        _bar.Value = process.ExitCode == 0 ? 100 : 0;

        if (process.ExitCode != 0)
        {
            AppendLog($"Atualizacao encerrou com codigo {process.ExitCode}. Verifique o log acima.");
            MessageBox.Show("A atualizacao nao foi concluida. Verifique o log exibido.", "Atualizar", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        else
        {
            AppendLog("Processo de atualizacao finalizado.");
        }

        _closeButton.Enabled = true;
    }

    private void PollDiskStatus()
    {
        try
        {
            var statusFile = Path.Combine(_installRoot, "update-status.json");
            if (!File.Exists(statusFile))
            {
                return;
            }

            using var stream = File.Open(statusFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var payload = JsonSerializer.Deserialize<DiskUpdateStatus>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (payload is null)
            {
                return;
            }

            _phaseLabel.Text = payload.Message is null
                ? ""
                : $"({payload.StepNumber}/{payload.TotalSteps}) {payload.Message}";
        }
        catch
        {
            // Arquivo pode estar sendo escrito no exato instante do polling — ignorar e tentar de novo.
        }
    }

    private void AppendLog(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLog(line));
            return;
        }

        _logBox.AppendText(line + Environment.NewLine);
    }

    private static string ResolveInstallRoot()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
            var imagePath = key?.GetValue("ImagePath") as string;
            if (!string.IsNullOrWhiteSpace(imagePath))
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
            // Segue para o default.
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

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private sealed class DiskUpdateStatus
    {
        public string? Status { get; set; }
        public string? Message { get; set; }
        public int StepNumber { get; set; }
        public int TotalSteps { get; set; }
    }
}

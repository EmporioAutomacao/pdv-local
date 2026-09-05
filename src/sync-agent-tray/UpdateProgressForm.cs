using System.Text.Json;
using SyncAgent.Tray.LocalApi;

namespace SyncAgent.Tray;

/// <summary>
/// Janela de progresso do botao "Atualizar App". Enquanto o SyncAgent (e sua
/// API local) estao no ar, faz polling de GET /update-status. Quando o
/// serviço entra na fase de aplicar a atualizacao (self-update.ps1), a API
/// local cai — nesse ponto passa a ler o arquivo update-status.json direto
/// do disco (escrito pelo proprio self-update.ps1) para continuar mostrando
/// progresso. O processo da bandeja é encerrado a força pelo
/// self-update.ps1 durante a troca de binarios; esta janela nao tenta
/// sobreviver a isso — a confirmacao final aparece como um balao quando a
/// bandeja é relançada automaticamente ao final do script.
/// </summary>
internal sealed class UpdateProgressForm : Form
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly LocalStatusClient _statusClient;
    private readonly string _targetVersion;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly Label _messageLabel;
    private readonly ProgressBar _bar;
    private readonly Button _closeButton;
    private bool _seenApplyingPhase;
    private bool _finished;

    public UpdateProgressForm(LocalStatusClient statusClient, string targetVersion, string? currentVersion = null)
    {
        _statusClient = statusClient;
        _targetVersion = targetVersion;

        Text = UpdateAvailableDialog.BuildTitle(currentVersion);
        Width = 440;
        Height = 160;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ControlBox = false;

        _messageLabel = new Label
        {
            Text = $"Iniciando atualizacao para a versao {targetVersion}...",
            Dock = DockStyle.Top,
            Height = 48,
            TextAlign = ContentAlignment.MiddleCenter,
            Padding = new Padding(12, 12, 12, 0)
        };
        _bar = new ProgressBar
        {
            Dock = DockStyle.Top,
            Height = 24,
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30
        };
        _closeButton = new Button
        {
            Text = "Fechar",
            Dock = DockStyle.Bottom,
            Height = 32,
            Visible = false
        };
        _closeButton.Click += (_, _) => Close();

        Controls.Add(_closeButton);
        Controls.Add(_bar);
        Controls.Add(_messageLabel);

        _pollTimer = new System.Windows.Forms.Timer { Interval = 700 };
        _pollTimer.Tick += async (_, _) => await PollAsync();

        Shown += async (_, _) => await StartAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pollTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task StartAsync()
    {
        try
        {
            var result = await _statusClient.TriggerUpdateAsync(_targetVersion);
            if (!result.Accepted)
            {
                ShowTerminal(result.Message, success: false);
                return;
            }
        }
        catch (Exception ex)
        {
            ShowTerminal($"Nao foi possivel iniciar a atualizacao: {ex.Message}", success: false);
            return;
        }

        _pollTimer.Start();
    }

    private async Task PollAsync()
    {
        if (_finished)
        {
            return;
        }

        try
        {
            var status = await _statusClient.GetUpdateStatusAsync();
            Apply(status.Status, status.Percent, status.Message, status.Error);
        }
        catch
        {
            // A API local caiu. O caso normal e o self-update.ps1 estar trocando
            // os binarios e reiniciando o servico - ele grava o progresso em
            // update-status.json, entao seguimos por ele. Antes isso so era
            // tentado depois de a janela ter visto a fase "Applying"; se a
            // conexao caisse antes disso (download rapido, poll com azar), a
            // janela congelava em "Baixando ... 97%" pra sempre. Agora tenta o
            // disco sempre que a API some e o arquivo esta fresco.
            var fromDisk = TryReadDiskStatus();
            if (fromDisk is not null)
            {
                _seenApplyingPhase = true;
                _bar.Style = ProgressBarStyle.Marquee;
                _messageLabel.Text = fromDisk.Value.Message;
                if (fromDisk.Value.Status is "completed")
                {
                    ShowTerminal("Atualizacao concluida. A bandeja sera reaberta em instantes.", success: true);
                }
                else if (fromDisk.Value.Status is "failed" or "rolled_back")
                {
                    ShowTerminal(fromDisk.Value.Message, success: false);
                }
            }
            else if (!_seenApplyingPhase)
            {
                // Sem status em disco e ainda na fase de download/verificacao:
                // provavelmente um soluco de rede momentaneo. A proxima poll
                // tenta de novo - nao trava a barra.
            }
        }
    }

    private void Apply(string status, int percent, string message, string? error)
    {
        _messageLabel.Text = message;

        switch (status)
        {
            case "Applying":
                _seenApplyingPhase = true;
                _bar.Style = ProgressBarStyle.Marquee;
                break;
            case "Downloading":
            case "Verifying":
                _bar.Style = ProgressBarStyle.Continuous;
                _bar.Value = Math.Clamp(percent, 0, 100);
                break;
            case "UpToDate":
                ShowTerminal(message, success: true);
                break;
            case "Failed":
                ShowTerminal(string.IsNullOrWhiteSpace(error) ? message : $"{message} ({error})", success: false);
                break;
            default:
                _bar.Style = ProgressBarStyle.Marquee;
                break;
        }
    }

    private void ShowTerminal(string message, bool success)
    {
        _finished = true;
        _pollTimer.Stop();
        _messageLabel.Text = message;
        _bar.Style = ProgressBarStyle.Continuous;
        _bar.Value = success ? 100 : 0;
        _closeButton.Visible = true;
        ControlBox = true;
    }

    private (string Status, string Message)? TryReadDiskStatus()
    {
        try
        {
            // SyncAgent.Tray.exe roda em <InstallRoot>\Sync\Tray\, dois
            // niveis abaixo da raiz — mesmo calculo usado pelo SelfUpdater.
            var installRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
            var statusFile = Path.Combine(installRoot, "update-status.json");
            if (!File.Exists(statusFile))
            {
                return null;
            }

            // update-status.json fica no disco depois da atualizacao (status
            // "completed"/"failed"). So confiar nele se for recente - senao uma
            // reabertura da janela mais tarde, com um soluco de rede, leria um
            // "completed" velho e mostraria sucesso a toa.
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(statusFile) > TimeSpan.FromMinutes(15))
            {
                return null;
            }

            using var stream = File.Open(statusFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var payload = JsonSerializer.Deserialize<DiskUpdateStatus>(stream, JsonOptions);
            if (payload is null)
            {
                return null;
            }

            return (payload.Status ?? "in_progress", payload.Message ?? "Aplicando atualizacao...");
        }
        catch
        {
            return null;
        }
    }

    private sealed class DiskUpdateStatus
    {
        public string? Status { get; set; }
        public string? Message { get; set; }
    }
}

using SyncAgent.Tray.LocalApi;
using System.Diagnostics;
using System.Text.Json;

namespace SyncAgent.Tray;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly LocalStatusClient _statusClient;
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _tenantItem;
    private readonly ToolStripMenuItem _instanceItem;
    private readonly ToolStripMenuItem _pendingItem;
    private readonly ToolStripMenuItem _deadLetterItem;
    private readonly ToolStripMenuItem _heartbeatItem;
    private readonly ToolStripMenuItem _reconciliationItem;
    private readonly ToolStripMenuItem _pendingUpdateMenuItem;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private AgentStatusResponse? _lastStatus;

    public TrayApplicationContext(LocalStatusClient statusClient)
    {
        _statusClient = statusClient;

        _statusItem = new ToolStripMenuItem("Status: iniciando...") { Enabled = false };
        _tenantItem = new ToolStripMenuItem("Tenant: -") { Enabled = false };
        _instanceItem = new ToolStripMenuItem("Instalacao: -") { Enabled = false };
        _pendingItem = new ToolStripMenuItem("Pendentes: -") { Enabled = false };
        _deadLetterItem = new ToolStripMenuItem("Dead-letter: -") { Enabled = false };
        _heartbeatItem = new ToolStripMenuItem("Heartbeat: -") { Enabled = false };
        _reconciliationItem = new ToolStripMenuItem("Reconciliacao: -") { Enabled = false };

        var openDashboardItem = new ToolStripMenuItem("Dashboard", null, (_, _) => OpenDashboard());
        var syncNowItem = new ToolStripMenuItem("Sincronizar agora", null, async (_, _) => await SyncNowAsync());
        var updateAppItem = new ToolStripMenuItem("Atualizar App", null, (_, _) => OpenUpdateProgress());
        _pendingUpdateMenuItem = new ToolStripMenuItem("Atualizacao pendente...", null, (_, _) => OpenPendingUpdateConfirmation())
        {
            Visible = false,
            Font = new Font(Control.DefaultFont, FontStyle.Bold),
        };
        var copyInstanceItem = new ToolStripMenuItem("Copiar ID da instalacao", null, (_, _) => CopyInstanceId());
        var refreshItem = new ToolStripMenuItem("Atualizar status", null, async (_, _) => await RefreshStatusAsync());
        var exitItem = new ToolStripMenuItem("Sair", null, (_, _) => ExitThread());

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "AraraSuite Sync",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip()
        };

        _notifyIcon.ContextMenuStrip.Items.AddRange(
        [
            _statusItem,
            _tenantItem,
            _instanceItem,
            _pendingItem,
            _deadLetterItem,
            _heartbeatItem,
            _reconciliationItem,
            new ToolStripSeparator(),
            openDashboardItem,
            syncNowItem,
            updateAppItem,
            _pendingUpdateMenuItem,
            refreshItem,
            copyInstanceItem,
            new ToolStripSeparator(),
            exitItem
        ]);

        _notifyIcon.DoubleClick += async (_, _) => await RefreshStatusAsync(showBalloon: true);

        _refreshTimer = new System.Windows.Forms.Timer
        {
            Interval = 30_000
        };
        _refreshTimer.Tick += async (_, _) => await RefreshStatusAsync();
        _refreshTimer.Start();

        _ = RefreshStatusAsync();
        ShowJustUpdatedBalloonIfApplicable();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshTimer.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _statusClient.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task RefreshStatusAsync(bool showBalloon = false)
    {
        try
        {
            _lastStatus = await _statusClient.GetStatusAsync();
            _statusItem.Text = $"Status: {_lastStatus.RuntimeStatus}";
            _tenantItem.Text = $"Tenant: {_lastStatus.ErpTenantId}";
            _instanceItem.Text = $"Instalacao: {_lastStatus.InstanceId}";
            _pendingItem.Text = $"Pendentes: {_lastStatus.PendingOutboxEvents}";
            _deadLetterItem.Text = $"Dead-letter: {_lastStatus.DeadLetterEvents}";
            _heartbeatItem.Text = $"Heartbeat: {FormatHeartbeat(_lastStatus)}";
            _reconciliationItem.Text = $"Reconciliacao: {FormatReconciliation(_lastStatus)}";
            _notifyIcon.Text = BuildNotifyText(_lastStatus.RuntimeStatus);
            CheckPendingUpdateConfirmation(_lastStatus);

            if (showBalloon)
            {
                _notifyIcon.ShowBalloonTip(
                    3000,
                    "AraraSuite Sync",
                    $"Online. Pendentes: {_lastStatus.PendingOutboxEvents}; Dead-letter: {_lastStatus.DeadLetterEvents}",
                    ToolTipIcon.Info);
            }
        }
        catch (Exception ex)
        {
            _statusItem.Text = "Status: offline";
            _tenantItem.Text = "Tenant: -";
            _instanceItem.Text = "Instalacao: -";
            _pendingItem.Text = "Pendentes: -";
            _deadLetterItem.Text = "Dead-letter: -";
            _heartbeatItem.Text = "Heartbeat: -";
            _reconciliationItem.Text = "Reconciliacao: -";
            _notifyIcon.Text = BuildNotifyText("Offline");
            _notifyIcon.ShowBalloonTip(
                3000,
                "AraraSuite Sync",
                $"Nao foi possivel consultar o servico local: {ex.Message}",
                ToolTipIcon.Warning);
        }
    }

    private async Task SyncNowAsync()
    {
        try
        {
            var response = await _statusClient.SyncNowAsync();
            _notifyIcon.ShowBalloonTip(
                3000,
                "AraraSuite Sync",
                response.Message,
                ToolTipIcon.Info);

            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            _notifyIcon.ShowBalloonTip(
                3000,
                "AraraSuite Sync",
                $"Falha ao sinalizar sincronizacao: {ex.Message}",
                ToolTipIcon.Error);
        }
    }

    private void OpenUpdateProgress()
    {
        string? selectedVersion;
        using (var check = new UpdateAvailableDialog(_statusClient, _lastStatus?.AgentVersion))
        {
            if (check.ShowDialog() != DialogResult.OK || string.IsNullOrWhiteSpace(check.SelectedVersion))
            {
                _ = RefreshStatusAsync();
                return;
            }

            selectedVersion = check.SelectedVersion;
        }

        using var form = new UpdateProgressForm(_statusClient, selectedVersion, _lastStatus?.AgentVersion);
        form.ShowDialog();
        _ = RefreshStatusAsync();
    }

    private void ShowJustUpdatedBalloonIfApplicable()
    {
        try
        {
            // SyncAgent.Tray.exe roda em <InstallRoot>\Sync\Tray\, dois
            // niveis abaixo da raiz. self-update.ps1 grava esse arquivo ao
            // concluir e relanca a bandeja logo em seguida — se ele existir
            // e for recente, foi essa relançada quem acabou de acontecer.
            var installRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
            var statusFile = Path.Combine(installRoot, "update-status.json");
            if (!File.Exists(statusFile))
            {
                return;
            }

            var info = new FileInfo(statusFile);
            if (DateTime.UtcNow - info.LastWriteTimeUtc > TimeSpan.FromMinutes(5))
            {
                return;
            }

            JustUpdatedStatus? payload;
            using (var stream = File.Open(statusFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                payload = JsonSerializer.Deserialize<JustUpdatedStatus>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }

            if (payload?.Status != "completed")
            {
                return;
            }

            // update-status.json fica em Program Files, onde um usuario comum
            // (a bandeja roda com o token padrao/nao elevado do usuario
            // logado) so tem leitura - renomear/apagar esse arquivo compartilhado
            // e negado mesmo concedendo ACL na propria janela, porque a
            // operacao depende de permissao no diretorio pai. Por isso o "ja
            // notificado" e controlado por um marcador no perfil do usuario.
            var markerFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AraraSuite", "last-update-notified.txt");
            var marker = $"{payload.Version}|{info.LastWriteTimeUtc:o}";
            if (File.Exists(markerFile) && File.ReadAllText(markerFile) == marker)
            {
                return;
            }

            _notifyIcon.ShowBalloonTip(
                5000,
                "AraraSuite Sync",
                $"Atualizacao concluida — versao {payload.Version}.",
                ToolTipIcon.Info);

            Directory.CreateDirectory(Path.GetDirectoryName(markerFile)!);
            File.WriteAllText(markerFile, marker);
        }
        catch
        {
            // Notificacao de conveniencia — falha aqui nunca deve impedir a bandeja de abrir.
        }
    }

    private sealed class JustUpdatedStatus
    {
        public string? Status { get; set; }
        public string? Version { get; set; }
    }

    /// <summary>
    /// Modo "Confirmar com o usuario" (contrato Sync 2.14.0): mostra a janela de
    /// confirmacao e um balao na primeira vez que uma atualizacao pendente aparecer.
    /// Um marcador em disco (mesmo raciocinio de <see cref="ShowJustUpdatedBalloonIfApplicable"/>)
    /// evita repetir o balao a cada poll de 30s ou apos a bandeja reiniciar - o item
    /// de menu continua disponivel o tempo todo enquanto a confirmacao estiver pendente.
    /// </summary>
    private void CheckPendingUpdateConfirmation(AgentStatusResponse status)
    {
        var pending = status.PendingUpdateConfirmation;
        _pendingUpdateMenuItem.Visible = pending is { AwaitingChoice: true };

        if (pending is not { AwaitingChoice: true })
        {
            return;
        }

        if (HasAlreadyNotifiedPendingUpdate(pending.Version))
        {
            return;
        }

        MarkPendingUpdateNotified(pending.Version);
        _notifyIcon.ShowBalloonTip(
            8000,
            "AraraSuite Sync",
            $"Atualizacao v{pending.Version} disponivel. Clique com o botao direito no icone e escolha \"Atualizacao pendente...\" para confirmar ou agendar - senao ela e aplicada automaticamente.",
            ToolTipIcon.Info);
    }

    private void OpenPendingUpdateConfirmation()
    {
        var pending = _lastStatus?.PendingUpdateConfirmation;
        if (pending is null)
        {
            return;
        }

        using var form = new PendingUpdateConfirmationForm(_statusClient, pending);
        form.ShowDialog();
        _ = RefreshStatusAsync();
    }

    private static string PendingUpdateMarkerFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AraraSuite", "pending-update-notified.txt");

    private static bool HasAlreadyNotifiedPendingUpdate(string version)
    {
        try
        {
            return File.Exists(PendingUpdateMarkerFile) && File.ReadAllText(PendingUpdateMarkerFile) == version;
        }
        catch
        {
            return false;
        }
    }

    private static void MarkPendingUpdateNotified(string version)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PendingUpdateMarkerFile)!);
            File.WriteAllText(PendingUpdateMarkerFile, version);
        }
        catch
        {
            // Notificacao de conveniencia — falha aqui nunca deve impedir a bandeja de continuar.
        }
    }

    private void CopyInstanceId()
    {
        if (_lastStatus is null)
        {
            return;
        }

        Clipboard.SetText(_lastStatus.InstanceId);
        _notifyIcon.ShowBalloonTip(
            2000,
            "AraraSuite Sync",
            "ID da instalacao copiado.",
            ToolTipIcon.Info);
    }

    private void OpenDashboard()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "http://127.0.0.1:47891/",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _notifyIcon.ShowBalloonTip(
                3000,
                "AraraSuite Sync",
                $"Nao foi possivel abrir o dashboard: {ex.Message}",
                ToolTipIcon.Warning);
        }
    }

    private string BuildNotifyText(string status)
    {
        var pending = _lastStatus?.PendingOutboxEvents.ToString() ?? "-";
        var deadLetter = _lastStatus?.DeadLetterEvents.ToString() ?? "-";
        return $"PDV Sync: {status} | Pendentes: {pending} | DL: {deadLetter}";
    }

    private static string FormatHeartbeat(AgentStatusResponse status)
    {
        if (status.LastHeartbeatAtUtc is null)
        {
            return "-";
        }

        var result = status.LastHeartbeatSucceeded == true ? "ok" : "falhou";
        return $"{result} / {status.LastHeartbeatConnectivity ?? "-"}";
    }

    private static string FormatReconciliation(AgentStatusResponse status)
    {
        if (status.LastReconciliationId is null)
        {
            return "-";
        }

        var completedAt = status.LastReconciliationCompletedAtUtc?.ToLocalTime().ToString("dd/MM HH:mm") ?? "-";
        return $"{status.LastReconciliationStatus ?? "-"} / {completedAt}";
    }
}

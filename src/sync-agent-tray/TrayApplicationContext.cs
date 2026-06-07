using SyncAgent.Tray.LocalApi;
using System.Diagnostics;

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

        var syncNowItem = new ToolStripMenuItem("Sincronizar agora", null, async (_, _) => await SyncNowAsync());
        var openHelpItem = new ToolStripMenuItem("Abrir ajuda", null, (_, _) => OpenHelp());
        var copyInstanceItem = new ToolStripMenuItem("Copiar ID da instalacao", null, (_, _) => CopyInstanceId());
        var refreshItem = new ToolStripMenuItem("Atualizar status", null, async (_, _) => await RefreshStatusAsync());
        var exitItem = new ToolStripMenuItem("Sair", null, (_, _) => ExitThread());

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "PDV Local Sync Agent",
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
            syncNowItem,
            refreshItem,
            openHelpItem,
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

            if (showBalloon)
            {
                _notifyIcon.ShowBalloonTip(
                    3000,
                    "PDV Local Sync Agent",
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
                "PDV Local Sync Agent",
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
                "PDV Local Sync Agent",
                response.Message,
                ToolTipIcon.Info);

            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            _notifyIcon.ShowBalloonTip(
                3000,
                "PDV Local Sync Agent",
                $"Falha ao sinalizar sincronizacao: {ex.Message}",
                ToolTipIcon.Error);
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
            "PDV Local Sync Agent",
            "ID da instalacao copiado.",
            ToolTipIcon.Info);
    }

    private void OpenHelp()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "http://127.0.0.1:47891/help",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _notifyIcon.ShowBalloonTip(
                3000,
                "PDV Local Sync Agent",
                $"Nao foi possivel abrir a ajuda: {ex.Message}",
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

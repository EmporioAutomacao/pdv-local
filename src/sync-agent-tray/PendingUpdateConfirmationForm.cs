using SyncAgent.Tray.LocalApi;

namespace SyncAgent.Tray;

/// <summary>
/// Mostrado quando o ERP pede uma atualizacao em modo "Confirmar com o
/// usuario" (contrato Sync 2.14.0, <c>GET /status</c> ->
/// <c>pending_update_confirmation</c>). O usuario pode aplicar na hora,
/// escolher um horario melhor, ou fechar sem decidir - nesse ultimo caso a
/// atualizacao aplica sozinha quando o prazo (padrao 5 min) ou o horario
/// agendado chegar, e esta janela continua acessivel pelo menu da bandeja.
/// </summary>
internal sealed class PendingUpdateConfirmationForm : Form
{
    private readonly LocalStatusClient _statusClient;
    private readonly PendingUpdateConfirmationInfo _info;
    private readonly Label _headerLabel;
    private readonly Label _notesLabel;
    private readonly Label _countdownLabel;
    private readonly DateTimePicker _scheduler;
    private readonly Button _confirmButton;
    private readonly Button _scheduleButton;
    private readonly Button _closeButton;
    private readonly System.Windows.Forms.Timer _countdownTimer;

    public PendingUpdateConfirmationForm(LocalStatusClient statusClient, PendingUpdateConfirmationInfo info)
    {
        _statusClient = statusClient;
        _info = info;

        Text = $"Atualizacao pendente - v{info.Version}";
        Width = 440;
        Height = 340;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        _headerLabel = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 40,
            Padding = new Padding(16, 12, 16, 0),
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            Text = $"Uma atualizacao (v{info.Version}) esta pronta para ser aplicada.",
        };
        _notesLabel = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 70,
            Padding = new Padding(16, 4, 16, 0),
            ForeColor = Color.DimGray,
            Text = string.IsNullOrWhiteSpace(info.ReleaseNotes) ? "Sem notas de versao." : info.ReleaseNotes,
        };
        _countdownLabel = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 32,
            Padding = new Padding(16, 4, 16, 0),
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
        };

        var confirmPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(16, 8, 16, 0),
        };
        _confirmButton = new Button { Text = "Atualizar agora", AutoSize = true };
        _confirmButton.Click += async (_, _) => await ConfirmNowAsync();
        confirmPanel.Controls.Add(_confirmButton);

        var schedulePanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(16, 12, 16, 0),
        };
        _scheduler = new DateTimePicker
        {
            Format = DateTimePickerFormat.Custom,
            CustomFormat = "dd/MM/yyyy HH:mm",
            Value = DateTime.Now.AddMinutes(30),
            MinDate = DateTime.Now.AddMinutes(1),
            Width = 160,
        };
        _scheduleButton = new Button { Text = "Agendar para...", AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        _scheduleButton.Click += async (_, _) => await ScheduleAsync();
        schedulePanel.Controls.Add(_scheduler);
        schedulePanel.Controls.Add(_scheduleButton);

        var buttonRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 44,
            Padding = new Padding(8),
        };
        _closeButton = new Button { Text = "Fechar", AutoSize = true };
        _closeButton.Click += (_, _) => Close();
        buttonRow.Controls.Add(_closeButton);

        Controls.Add(schedulePanel);
        Controls.Add(confirmPanel);
        Controls.Add(_countdownLabel);
        Controls.Add(_notesLabel);
        Controls.Add(_headerLabel);
        Controls.Add(buttonRow);

        _countdownTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _countdownTimer.Tick += (_, _) => UpdateCountdown();
        _countdownTimer.Start();
        UpdateCountdown();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _countdownTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private void UpdateCountdown()
    {
        var target = _info.ScheduledAtUtc ?? _info.DeadlineAtUtc;
        if (target is null)
        {
            _countdownLabel.Text = "Sera aplicada automaticamente em breve se nada for feito.";
            return;
        }

        var remaining = target.Value - DateTimeOffset.UtcNow;
        var label = _info.ScheduledAtUtc is not null ? "Agendada para" : "Aplica automaticamente em";
        _countdownLabel.Text = remaining <= TimeSpan.Zero
            ? "Aplicando a qualquer momento..."
            : $"{label} {target.Value.ToLocalTime():dd/MM HH:mm} (faltam {FormatRemaining(remaining)})";
    }

    private static string FormatRemaining(TimeSpan remaining)
    {
        return remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours}h{remaining.Minutes:00}m"
            : $"{(int)remaining.TotalMinutes}m{remaining.Seconds:00}s";
    }

    private async Task ConfirmNowAsync()
    {
        _confirmButton.Enabled = false;
        _scheduleButton.Enabled = false;
        try
        {
            await _statusClient.ConfirmPendingUpdateAsync();
            MessageBox.Show(
                "Atualizacao confirmada. O Sync e o PDV serao reiniciados em instantes.",
                "Atualizacao pendente",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Nao foi possivel confirmar a atualizacao agora: {ex.Message}",
                "Atualizacao pendente",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            _confirmButton.Enabled = true;
            _scheduleButton.Enabled = true;
        }
    }

    private async Task ScheduleAsync()
    {
        _confirmButton.Enabled = false;
        _scheduleButton.Enabled = false;
        try
        {
            var scheduledAt = new DateTimeOffset(_scheduler.Value);
            var result = await _statusClient.SchedulePendingUpdateAsync(scheduledAt);
            MessageBox.Show(
                result.Message,
                "Atualizacao pendente",
                MessageBoxButtons.OK,
                result.Accepted ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            if (result.Accepted)
            {
                Close();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Nao foi possivel agendar a atualizacao: {ex.Message}",
                "Atualizacao pendente",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            _confirmButton.Enabled = true;
            _scheduleButton.Enabled = true;
        }
    }
}

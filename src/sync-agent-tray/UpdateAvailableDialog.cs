using SyncAgent.Tray.LocalApi;

namespace SyncAgent.Tray;

/// <summary>
/// Primeira tela do botao "Atualizar App": consulta GET /update-check e mostra
/// a versao instalada e a versao disponivel no ERP. So retorna
/// <see cref="DialogResult.OK"/> (seguido pela <see cref="UpdateProgressForm"/>)
/// quando ha uma versao nova e o usuario confirma. Se ja estiver atualizado ou
/// a consulta falhar, mostra a mensagem e fecha sem aplicar nada.
/// </summary>
internal sealed class UpdateAvailableDialog : Form
{
    private readonly LocalStatusClient _statusClient;
    private readonly Label _messageLabel;
    private readonly ProgressBar _bar;
    private readonly Button _confirmButton;
    private readonly Button _cancelButton;

    public UpdateAvailableDialog(LocalStatusClient statusClient)
    {
        _statusClient = statusClient;

        Text = "Atualizar App";
        Width = 460;
        Height = 200;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        _messageLabel = new Label
        {
            Text = "Consultando a versao disponivel...",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(16, 12, 16, 8)
        };
        _bar = new ProgressBar
        {
            Dock = DockStyle.Top,
            Height = 20,
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30
        };

        var buttonRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 44,
            Padding = new Padding(8)
        };
        _confirmButton = new Button
        {
            Text = "Atualizar agora",
            AutoSize = true,
            Visible = false,
            DialogResult = DialogResult.OK
        };
        _cancelButton = new Button
        {
            Text = "Fechar",
            AutoSize = true,
            DialogResult = DialogResult.Cancel
        };
        buttonRow.Controls.Add(_confirmButton);
        buttonRow.Controls.Add(_cancelButton);

        Controls.Add(_messageLabel);
        Controls.Add(buttonRow);
        Controls.Add(_bar);

        AcceptButton = _cancelButton;
        CancelButton = _cancelButton;

        Shown += async (_, _) => await CheckAsync();
    }

    private async Task CheckAsync()
    {
        UpdateCheckResponse result;
        try
        {
            result = await _statusClient.CheckUpdateAsync();
        }
        catch (Exception ex)
        {
            ShowInfo($"Nao foi possivel consultar a versao disponivel: {ex.Message}");
            return;
        }

        if (!string.IsNullOrEmpty(result.Error))
        {
            ShowInfo(DescribeError(result.Error, result.CurrentVersion));
            return;
        }

        if (!result.UpdateAvailable || string.IsNullOrWhiteSpace(result.LatestVersion))
        {
            ShowInfo($"Voce ja esta na versao mais recente ({result.CurrentVersion}).");
            return;
        }

        _bar.Visible = false;
        var text = $"Versao instalada:   {result.CurrentVersion}\r\n"
                 + $"Versao disponivel:  {result.LatestVersion}";
        if (!string.IsNullOrWhiteSpace(result.ReleaseNotes))
        {
            text += $"\r\n\r\n{result.ReleaseNotes.Trim()}";
        }
        text += "\r\n\r\nO Sync e o PDV serao reiniciados durante a atualizacao.";
        _messageLabel.Text = text;

        _confirmButton.Visible = true;
        _cancelButton.Text = "Agora nao";
        AcceptButton = _confirmButton;
    }

    private void ShowInfo(string message)
    {
        _bar.Visible = false;
        _messageLabel.Text = message;
        _confirmButton.Visible = false;
        _cancelButton.Text = "Fechar";
    }

    private static string DescribeError(string code, string currentVersion) => code switch
    {
        "no_package_published" => "Nenhuma versao foi publicada no ERP ainda.",
        "not_provisioned" => "Esta instalacao ainda nao foi ativada no ERP.",
        "self_update_disabled" => $"A atualizacao automatica esta desabilitada nesta instalacao (versao atual {currentVersion}).",
        "invalid_response" => "O ERP respondeu de forma inesperada a consulta de versao.",
        _ when code.StartsWith("http_", StringComparison.Ordinal) => $"O ERP retornou um erro ({code}) ao consultar a versao disponivel.",
        _ => $"Nao foi possivel consultar a versao disponivel ({code}).",
    };
}

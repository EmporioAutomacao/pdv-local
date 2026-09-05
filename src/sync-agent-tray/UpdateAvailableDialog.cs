using SyncAgent.Tray.LocalApi;

namespace SyncAgent.Tray;

/// <summary>
/// Primeira tela do botao "Atualizar App": consulta GET /update-check e mostra
/// a versao instalada e a lista de versoes permitidas pelo ERP (curadoria por
/// cliente). O usuario escolhe uma da lista -- nunca uma anterior a instalada
/// (o ERP ja nao devolve isso, e o SyncAgent revalida de novo antes de
/// aplicar) e nunca uma marcada como incompativel com o ERP local. So
/// retorna <see cref="DialogResult.OK"/> (seguido pela
/// <see cref="UpdateProgressForm"/>, com <see cref="SelectedVersion"/>)
/// quando ha uma opcao aplicavel e o usuario confirma. Se nao houver nenhuma
/// opcao ou a consulta falhar, mostra a mensagem e fecha sem aplicar nada.
/// </summary>
internal sealed class UpdateAvailableDialog : Form
{
    private readonly LocalStatusClient _statusClient;
    private readonly Label _headerLabel;
    private readonly Label _messageLabel;
    private readonly ProgressBar _bar;
    private readonly ListBox _packageList;
    private readonly Label _detailLabel;
    private readonly Button _confirmButton;
    private readonly Button _cancelButton;

    public string? SelectedVersion { get; private set; }

    public UpdateAvailableDialog(LocalStatusClient statusClient)
    {
        _statusClient = statusClient;

        Text = "Atualizar App";
        Width = 480;
        Height = 360;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        _headerLabel = new Label
        {
            Text = "Consultando as versoes disponiveis...",
            Dock = DockStyle.Top,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(16, 12, 16, 0)
        };
        _bar = new ProgressBar
        {
            Dock = DockStyle.Top,
            Height = 20,
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30
        };
        _messageLabel = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 60,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(16, 8, 16, 0),
            Visible = false
        };
        _packageList = new ListBox
        {
            Dock = DockStyle.Top,
            Height = 120,
            Margin = new Padding(16, 8, 16, 0),
            IntegralHeight = false,
            Visible = false
        };
        _packageList.SelectedIndexChanged += (_, _) => OnSelectionChanged();
        _detailLabel = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 70,
            TextAlign = ContentAlignment.TopLeft,
            Padding = new Padding(16, 8, 16, 0),
            ForeColor = Color.DimGray,
            Visible = false
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
            Enabled = false,
            DialogResult = DialogResult.OK
        };
        _confirmButton.Click += (_, _) => SelectedVersion = (_packageList.SelectedItem as PackageItem)?.Version;
        _cancelButton = new Button
        {
            Text = "Fechar",
            AutoSize = true,
            DialogResult = DialogResult.Cancel
        };
        buttonRow.Controls.Add(_confirmButton);
        buttonRow.Controls.Add(_cancelButton);

        // Ordem de inclusao == ordem visual de baixo pra cima dentro de um
        // Dock.Top empilhado: adicionar por ultimo o que deve ficar mais acima.
        Controls.Add(_detailLabel);
        Controls.Add(_packageList);
        Controls.Add(_messageLabel);
        Controls.Add(buttonRow);
        Controls.Add(_bar);
        Controls.Add(_headerLabel);

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
            ShowInfo($"Nao foi possivel consultar as versoes disponiveis: {ex.Message}");
            return;
        }

        if (!string.IsNullOrEmpty(result.Error))
        {
            ShowInfo(DescribeError(result.Error, result.CurrentVersion));
            return;
        }

        if (result.Packages.Count == 0)
        {
            ShowInfo($"Voce ja esta na versao mais recente permitida ({result.CurrentVersion}).");
            return;
        }

        _bar.Visible = false;
        _headerLabel.Text = $"Versao instalada: {result.CurrentVersion}. Escolha para qual versao atualizar:";

        _packageList.Visible = true;
        _packageList.Items.Clear();
        foreach (var package in result.Packages)
        {
            _packageList.Items.Add(new PackageItem(package));
        }

        _detailLabel.Visible = true;
        _confirmButton.Visible = true;
        _cancelButton.Text = "Agora nao";

        // Pre-seleciona a primeira opcao nao-bloqueada, se houver.
        var firstAvailable = _packageList.Items.Cast<PackageItem>()
            .Select((item, index) => (item, index))
            .FirstOrDefault(pair => !pair.item.Package.Blocked);
        _packageList.SelectedIndex = firstAvailable.item is not null ? firstAvailable.index : 0;
    }

    private void OnSelectionChanged()
    {
        if (_packageList.SelectedItem is not PackageItem selected)
        {
            _confirmButton.Enabled = false;
            _detailLabel.Text = string.Empty;
            return;
        }

        var package = selected.Package;
        if (package.Blocked)
        {
            _confirmButton.Enabled = false;
            var minimo = string.IsNullOrWhiteSpace(package.ErpMinimo) ? "uma versao mais recente" : package.ErpMinimo;
            _detailLabel.Text = $"Bloqueada: requer o ERP na versao {minimo} ou superior. "
                + "Atualize o ERP antes de poder aplicar esta versao.";
            _detailLabel.ForeColor = Color.Firebrick;
        }
        else
        {
            _confirmButton.Enabled = true;
            _detailLabel.Text = string.IsNullOrWhiteSpace(package.ReleaseNotes)
                ? "Sem notas de versao."
                : package.ReleaseNotes;
            _detailLabel.ForeColor = Color.DimGray;
        }
    }

    private void ShowInfo(string message)
    {
        _bar.Visible = false;
        _headerLabel.Text = "Atualizar App";
        _messageLabel.Visible = true;
        _messageLabel.Text = message;
        _confirmButton.Visible = false;
        _cancelButton.Text = "Fechar";
    }

    private static string DescribeError(string code, string currentVersion) => code switch
    {
        "no_package_published" => "Nenhuma versao foi publicada no ERP ainda.",
        "not_provisioned" => "Esta instalacao ainda nao foi ativada no ERP.",
        "invalid_package_sha256" => "O pacote publicado no ERP tem um Sha256 invalido (parece ser uma URL, nao o hash). "
            + "Peca para corrigirem em API de Sincronizacao > Pacotes de atualizacao.",
        "self_update_disabled" => $"A atualizacao automatica esta desabilitada nesta instalacao (versao atual {currentVersion}).",
        "invalid_response" => "O ERP respondeu de forma inesperada a consulta de versao.",
        _ when code.StartsWith("http_", StringComparison.Ordinal) => $"O ERP retornou um erro ({code}) ao consultar a versao disponivel.",
        _ => $"Nao foi possivel consultar a versao disponivel ({code}).",
    };

    private sealed record PackageItem(UpdateCheckPackage Package)
    {
        public string Version => Package.Version;

        public override string ToString() => Package.Blocked
            ? $"v{Package.Version}  —  bloqueada (ERP incompativel)"
            : $"v{Package.Version}";
    }
}

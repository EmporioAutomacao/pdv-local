using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using PdvLocal.Core;
using static PdvLocal.App.PdvUiFormatting;

namespace PdvLocal.App;

public partial class CashSessionWindow : Window
{
    private readonly PdvOperator _operator;
    private readonly PdvCashSessionRepository _cashSessionRepository;
    private readonly PdvCashMovementRepository _cashMovementRepository;
    private readonly PdvOperatorRepository _operatorRepository;
    private readonly PdvPaymentCatalogRepository _paymentCatalogRepository;
    private readonly bool _hasPendingSale;
    private readonly ObservableCollection<CashSummaryRow> _summaryRows = [];
    private readonly ObservableCollection<ClosingCountRow> _closingCountRows = [];
    private bool _closingCountsLoaded;

    public PdvCashSession? CurrentSession { get; private set; }

    public CashSessionWindow(
        PdvOperator @operator,
        PdvCashSession? currentSession,
        PdvCashSessionRepository cashSessionRepository,
        PdvCashMovementRepository cashMovementRepository,
        PdvOperatorRepository operatorRepository,
        PdvPaymentCatalogRepository paymentCatalogRepository,
        bool hasPendingSale)
    {
        _operator = @operator;
        CurrentSession = currentSession;
        _cashSessionRepository = cashSessionRepository;
        _cashMovementRepository = cashMovementRepository;
        _operatorRepository = operatorRepository;
        _paymentCatalogRepository = paymentCatalogRepository;
        _hasPendingSale = hasPendingSale;
        InitializeComponent();
        CashOperatorText.Text = $"Operador: {@operator.DisplayName} ({@operator.Login})";
        CashSummaryGrid.ItemsSource = _summaryRows;
        ClosingCountsGrid.ItemsSource = _closingCountRows;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await RunAsync(ApplyStateAsync);
        if (CurrentSession is null)
        {
            OpeningAmountTextBox.Focus();
            OpeningAmountTextBox.SelectAll();
        }
    }

    private async Task ApplyStateAsync()
    {
        var hasSession = CurrentSession is not null;
        OpenStatePanel.Visibility = hasSession ? Visibility.Collapsed : Visibility.Visible;
        OpenedStatePanel.Visibility = hasSession ? Visibility.Visible : Visibility.Collapsed;

        if (!hasSession)
        {
            return;
        }

        CurrentCashSessionValue.Text = $"{CurrentSession!.CashSessionId} / {CurrentSession.Status}";
        await RefreshCashSummaryAsync();
        await LoadClosingCountRowsAsync();

        var operationsAllowed = !_hasPendingSale;
        CashMovementAmountTextBox.IsEnabled = operationsAllowed;
        CashMovementObservationTextBox.IsEnabled = operationsAllowed;
        CashSupplyButton.IsEnabled = operationsAllowed;
        CashWithdrawalButton.IsEnabled = operationsAllowed;
        ClosingCountsGrid.IsEnabled = operationsAllowed;
        CloseCashButton.IsEnabled = operationsAllowed;
        if (_hasPendingSale)
        {
            ShowMessage("Ha venda em andamento. Finalize ou limpe a venda antes de fechar ou movimentar o caixa.", isError: true);
        }
    }

    private async Task RefreshCashSummaryAsync()
    {
        if (CurrentSession is null)
        {
            return;
        }

        var summary = await _cashMovementRepository.GetSummaryAsync(
            CurrentSession.CashSessionId,
            CancellationToken.None);

        // Fechamento cego: o resumo visivel nao revela dinheiro esperado nem o
        // vendido por especie — esses valores so aparecem no relatorio final.
        _summaryRows.Clear();
        _summaryRows.Add(new CashSummaryRow("Abertura (fundo de troco)", FormatMoney(summary.OpeningAmount)));
        _summaryRows.Add(new CashSummaryRow($"Vendas ({summary.SaleCount})", FormatMoney(summary.TotalSalesAmount)));
        _summaryRows.Add(new CashSummaryRow("Suprimentos", FormatMoney(summary.SupplyAmount)));
        _summaryRows.Add(new CashSummaryRow("Sangrias", FormatMoney(summary.WithdrawalAmount)));
    }

    private async Task LoadClosingCountRowsAsync()
    {
        if (_closingCountsLoaded)
        {
            return;
        }

        var species = await _paymentCatalogRepository.GetActiveSpeciesAsync(CancellationToken.None);
        _closingCountRows.Clear();

        foreach (var item in species.OrderByDescending(s => s.Kind == "cash").ThenBy(s => s.Name))
        {
            _closingCountRows.Add(new ClosingCountRow
            {
                SpeciesName = item.Name,
                Kind = item.Kind,
                CountedText = "0,00"
            });
        }

        if (_closingCountRows.Count == 0)
        {
            _closingCountRows.Add(new ClosingCountRow { SpeciesName = "Dinheiro", Kind = "cash", CountedText = "0,00" });
        }

        _closingCountsLoaded = true;
    }

    private void OpeningAmountTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        OpenCashButton_Click(OpenCashButton, e);
    }

    private async void OpenCashButton_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            var openingAmount = ParseMoney(OpeningAmountTextBox.Text, "Valor de abertura");
            var existingCashSession = await _cashSessionRepository.FindOpenByOperatorAsync(
                _operator.OperatorId,
                CancellationToken.None);
            if (existingCashSession is not null)
            {
                CurrentSession = existingCashSession;
                DialogResult = true;
                return;
            }

            await _cashSessionRepository.OpenAsync(
                new OpenCashSessionCommand(_operator.OperatorId, openingAmount, null),
                CancellationToken.None);
            CurrentSession = await _cashSessionRepository.FindOpenByOperatorAsync(
                _operator.OperatorId,
                CancellationToken.None);
            DialogResult = true;
        });
    }

    private async void CloseCashButton_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            EnsureSession();
            ClosingCountsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);

            var counted = _closingCountRows
                .Select(row => new PdvClosingCount(
                    row.SpeciesName,
                    row.Kind,
                    ParseMoney(row.CountedText, $"Contagem de {row.SpeciesName}")))
                .ToList();

            var session = CurrentSession!;
            var summary = await _cashMovementRepository.GetSummaryAsync(session.CashSessionId, CancellationToken.None);
            var movements = await _cashMovementRepository.GetMovementsAsync(session.CashSessionId, CancellationToken.None);
            var entries = PdvCashClosing.BuildClosingCounts(counted, summary);
            var closingAmount = entries.FirstOrDefault(entry => entry.Kind == "cash")?.CountedAmount ?? 0;

            await _cashSessionRepository.CloseAsync(
                new CloseCashSessionCommand(
                    session.CashSessionId,
                    closingAmount,
                    BuildCloseCashNotes(summary, closingAmount),
                    entries),
                CancellationToken.None);

            CurrentSession = null;
            _closingCountsLoaded = false;
            await ApplyStateAsync();
            OpeningAmountTextBox.Text = "0,00";

            var cashDifference = entries.FirstOrDefault(entry => entry.Kind == "cash")?.Difference ?? 0;
            ShowMessage($"Caixa fechado. Diferenca em dinheiro: {FormatMoney(cashDifference)}.", isError: false);

            new CashCloseReportWindow(
                new CashCloseReportData(
                    session.CashSessionId,
                    _operator.DisplayName,
                    session.OpenedAtUtc,
                    DateTimeOffset.Now,
                    summary,
                    entries,
                    movements))
            { Owner = this }.ShowDialog();
        });
    }

    private async void CashSupplyButton_Click(object sender, RoutedEventArgs e)
    {
        await RecordCashMovementAsync("supply");
    }

    private async void CashWithdrawalButton_Click(object sender, RoutedEventArgs e)
    {
        await RecordCashMovementAsync("withdrawal");
    }

    private async Task RecordCashMovementAsync(string movementType)
    {
        await RunAsync(async () =>
        {
            EnsureSession();
            var movementName = movementType == "supply" ? "suprimento" : "sangria";
            var amount = ParseMoney(CashMovementAmountTextBox.Text, $"Valor de {movementName}");
            var observation = CashMovementObservationTextBox.Text.Trim();
            if (observation.Length == 0)
            {
                throw new ArgumentException($"Informe a observacao do {movementName} (ex.: motivo do reforco ou retirada).");
            }

            var authorization = SupervisorAuthorizationDialog.Request(this, _operatorRepository, $"registrar {movementName}")
                ?? throw new InvalidOperationException($"Autorizacao de supervisor cancelada para registrar {movementName}.");

            if (movementType == "withdrawal")
            {
                var summary = await _cashMovementRepository.GetSummaryAsync(
                    CurrentSession!.CashSessionId,
                    CancellationToken.None);
                if (amount > summary.ExpectedCashAmount)
                {
                    throw new ArgumentException("Sangria nao pode ser maior que o dinheiro esperado no caixa.");
                }
            }

            var movementId = await _cashMovementRepository.InsertAsync(
                new PdvCashMovementCommand(
                    CurrentSession!.CashSessionId,
                    _operator.OperatorId,
                    authorization.Supervisor.OperatorId,
                    movementType,
                    amount,
                    authorization.Reason,
                    observation),
                CancellationToken.None);

            CashMovementAmountTextBox.Text = "0,00";
            CashMovementObservationTextBox.Text = "";
            await RefreshCashSummaryAsync();
            ShowMessage($"{Capitalize(movementName)} registrado com auditoria. ID: {movementId}.", isError: false);
        });
    }

    private static string BuildCloseCashNotes(
        PdvCashSessionSummary summary,
        decimal closingAmount)
    {
        var difference = closingAmount - summary.ExpectedCashAmount;
        return string.Join(
            " | ",
            "Fechado pelo PDV App",
            $"abertura={FormatDecimal(summary.OpeningAmount)}",
            $"vendas={FormatDecimal(summary.TotalSalesAmount)}",
            $"dinheiro_vendas={FormatDecimal(summary.CashSalesAmount)}",
            $"suprimentos={FormatDecimal(summary.SupplyAmount)}",
            $"sangrias={FormatDecimal(summary.WithdrawalAmount)}",
            $"dinheiro_esperado={FormatDecimal(summary.ExpectedCashAmount)}",
            $"dinheiro_informado={FormatDecimal(closingAmount)}",
            $"diferenca={FormatDecimal(difference)}");
    }

    private async Task RunAsync(Func<Task> operation)
    {
        try
        {
            HideMessage();
            await operation();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Npgsql.NpgsqlException or TimeoutException)
        {
            ShowMessage(ex.Message, isError: true);
        }
    }

    private void EnsureSession()
    {
        if (CurrentSession is null)
        {
            throw new InvalidOperationException("Nao ha caixa aberto.");
        }
    }

    private void ShowMessage(string message, bool isError)
    {
        CashMessageText.Text = message;
        if (isError)
        {
            CashMessageText.Foreground = BrushFromHex("#E53E3E");
        }
        else
        {
            CashMessageText.SetResourceReference(ForegroundProperty, "SuccessText");
        }

        CashMessageText.Visibility = Visibility.Visible;
    }

    private void HideMessage()
    {
        CashMessageText.Visibility = Visibility.Collapsed;
    }
}

internal sealed record CashSummaryRow(string Label, string ValueText);

internal sealed class ClosingCountRow
{
    public string SpeciesName { get; init; } = string.Empty;
    public string Kind { get; init; } = "other";
    public string CountedText { get; set; } = "0,00";
}

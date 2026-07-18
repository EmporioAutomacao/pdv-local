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
    private readonly bool _hasPendingSale;

    public PdvCashSession? CurrentSession { get; private set; }

    public CashSessionWindow(
        PdvOperator @operator,
        PdvCashSession? currentSession,
        PdvCashSessionRepository cashSessionRepository,
        PdvCashMovementRepository cashMovementRepository,
        PdvOperatorRepository operatorRepository,
        bool hasPendingSale)
    {
        _operator = @operator;
        CurrentSession = currentSession;
        _cashSessionRepository = cashSessionRepository;
        _cashMovementRepository = cashMovementRepository;
        _operatorRepository = operatorRepository;
        _hasPendingSale = hasPendingSale;
        InitializeComponent();
        CashOperatorText.Text = $"Operador: {@operator.DisplayName} ({@operator.Login})";
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

        var operationsAllowed = !_hasPendingSale;
        CashMovementAmountTextBox.IsEnabled = operationsAllowed;
        CashSupplyButton.IsEnabled = operationsAllowed;
        CashWithdrawalButton.IsEnabled = operationsAllowed;
        ClosingAmountTextBox.IsEnabled = operationsAllowed;
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
        CashSummaryValue.Text = FormatCashSummary(summary);
        ClosingAmountTextBox.Text = FormatDecimal(summary.ExpectedCashAmount);
    }

    private void OpeningAmountTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        OpenCashButton_Click(OpenCashButton, e);
    }

    private void ClosingAmountTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        CloseCashButton_Click(CloseCashButton, e);
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
            var closingAmount = ParseMoney(ClosingAmountTextBox.Text, "Valor de fechamento");
            var summary = await _cashMovementRepository.GetSummaryAsync(
                CurrentSession!.CashSessionId,
                CancellationToken.None);
            var difference = closingAmount - summary.ExpectedCashAmount;
            await _cashSessionRepository.CloseAsync(
                new CloseCashSessionCommand(
                    CurrentSession.CashSessionId,
                    closingAmount,
                    BuildCloseCashNotes(summary, closingAmount)),
                CancellationToken.None);
            CurrentSession = null;
            await ApplyStateAsync();
            OpeningAmountTextBox.Text = "0,00";
            ShowMessage($"Caixa fechado. Diferenca: {FormatMoney(difference)}.", isError: false);
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
                    authorization.Reason),
                CancellationToken.None);

            CashMovementAmountTextBox.Text = "0,00";
            await RefreshCashSummaryAsync();
            ShowMessage($"{Capitalize(movementName)} registrado com auditoria. ID: {movementId}.", isError: false);
        });
    }

    private static string FormatCashSummary(PdvCashSessionSummary summary)
    {
        var speciesText = summary.PaymentSpecies.Count == 0
            ? "-"
            : Environment.NewLine + string.Join(
                Environment.NewLine,
                summary.PaymentSpecies.Select(item => $"  {item.SpeciesName}: {FormatMoney(item.Amount)}"));

        return string.Join(
            Environment.NewLine,
            $"Abertura: {FormatMoney(summary.OpeningAmount)}",
            $"Vendas: {FormatMoney(summary.TotalSalesAmount)} ({summary.SaleCount})",
            $"Dinheiro em vendas: {FormatMoney(summary.CashSalesAmount)}",
            $"Suprimentos: {FormatMoney(summary.SupplyAmount)}",
            $"Sangrias: {FormatMoney(summary.WithdrawalAmount)}",
            $"Dinheiro esperado: {FormatMoney(summary.ExpectedCashAmount)}",
            $"Por especie: {speciesText}");
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

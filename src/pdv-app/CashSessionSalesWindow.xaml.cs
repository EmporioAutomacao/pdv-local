using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PdvLocal.Core;

namespace PdvLocal.App;

public partial class CashSessionSalesWindow : Window
{
    private static readonly CultureInfo BrazilianCulture = CultureInfo.GetCultureInfo("pt-BR");

    private readonly PdvSaleRepository _saleRepository;
    private readonly PdvOperatorRepository _operatorRepository;
    private readonly PdvOperator _currentOperator;
    private readonly Guid _cashSessionId;

    private SaleSummaryRow? _pendingCancelRow;

    internal CashSessionSalesWindow(
        PdvSaleRepository saleRepository,
        PdvOperatorRepository operatorRepository,
        PdvOperator currentOperator,
        Guid cashSessionId,
        string sessionTitle)
    {
        _saleRepository = saleRepository;
        _operatorRepository = operatorRepository;
        _currentOperator = currentOperator;
        _cashSessionId = cashSessionId;
        InitializeComponent();
        SessionInfoText.Text = sessionTitle;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await LoadSalesAsync();

    private async void RefreshSalesButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshSalesButton.IsEnabled = false;
        HideCancellationPanel();
        try
        {
            await LoadSalesAsync();
        }
        finally
        {
            RefreshSalesButton.IsEnabled = true;
        }
    }

    private async Task LoadSalesAsync()
    {
        LoadingText.Visibility = Visibility.Visible;
        SummaryCountText.Text = string.Empty;
        SummaryTotalText.Text = string.Empty;

        try
        {
            var sales = await _saleRepository.GetByCashSessionAsync(_cashSessionId, CancellationToken.None);

            SalesGrid.ItemsSource = sales
                .Select(s => new SaleSummaryRow(s))
                .ToList();

            LoadingText.Visibility = Visibility.Collapsed;

            var completed = sales.Where(s => s.Status == "completed").ToList();
            var count = completed.Count;
            var total = completed.Sum(s => s.TotalAmount);

            SummaryCountText.Text = count == 0
                ? "Nenhuma venda finalizada nesta sessao."
                : $"{count} {(count == 1 ? "venda finalizada" : "vendas finalizadas")}";

            SummaryTotalText.Text = count == 0
                ? string.Empty
                : $"Total: {total.ToString("C", BrazilianCulture)}";
        }
        catch (Exception ex)
        {
            LoadingText.Text = $"Erro ao carregar vendas: {ex.Message}";
        }
    }

    private void SalesGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SalesGrid.SelectedItem is SaleSummaryRow row && row.IsCompleted)
        {
            _pendingCancelRow = row;
            CancelTitleText.Text = $"Cancelar venda {row.SaleNumber}";
            CancelSupervisorTextBox.Clear();
            CancelReasonTextBox.Clear();
            CancelErrorText.Visibility = Visibility.Collapsed;
            CancellationPanel.Visibility = Visibility.Visible;
            CancelSupervisorTextBox.Focus();
        }
        else
        {
            HideCancellationPanel();
        }
    }

    private async void ConfirmCancelButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteCancellationAsync();
    }

    private void CancelSupervisorTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CancelReasonTextBox.Focus();
        }
    }

    private async void CancelReasonTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await ExecuteCancellationAsync();
        }
    }

    private async Task ExecuteCancellationAsync()
    {
        if (_pendingCancelRow is null)
            return;

        var supervisorLogin = CancelSupervisorTextBox.Text.Trim();
        var reason = CancelReasonTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(supervisorLogin))
        {
            ShowCancelError("Informe o login do supervisor.");
            CancelSupervisorTextBox.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            ShowCancelError("Informe o motivo do cancelamento.");
            CancelReasonTextBox.Focus();
            return;
        }

        try
        {
            var supervisor = await _operatorRepository.FindActiveByLoginAsync(supervisorLogin, CancellationToken.None);
            if (supervisor is null)
            {
                ShowCancelError($"Supervisor ativo nao encontrado: {supervisorLogin}.");
                CancelSupervisorTextBox.Focus();
                return;
            }

            if (!PdvValidation.IsSupervisorRole(supervisor.Role))
            {
                ShowCancelError($"Operador '{supervisorLogin}' nao possui papel de supervisor/admin.");
                CancelSupervisorTextBox.Focus();
                return;
            }

            await _saleRepository.CancelSaleAsync(
                _pendingCancelRow.SaleId,
                _cashSessionId,
                _currentOperator.OperatorId,
                supervisor.OperatorId,
                reason,
                CancellationToken.None);

            HideCancellationPanel();
            await LoadSalesAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Npgsql.NpgsqlException)
        {
            ShowCancelError(ex.Message);
        }
    }

    private void DiscardCancelButton_Click(object sender, RoutedEventArgs e)
    {
        HideCancellationPanel();
        SalesGrid.SelectedItem = null;
    }

    private void HideCancellationPanel()
    {
        _pendingCancelRow = null;
        CancellationPanel.Visibility = Visibility.Collapsed;
        CancelErrorText.Visibility = Visibility.Collapsed;
    }

    private void ShowCancelError(string message)
    {
        CancelErrorText.Text = message;
        CancelErrorText.Visibility = Visibility.Visible;
    }

    private void AutoSelectFirstSaleBtn_Click(object sender, RoutedEventArgs e)
    {
        if (SalesGrid.ItemsSource is List<SaleSummaryRow> rows)
        {
            var first = rows.FirstOrDefault(r => r.IsCompleted);
            if (first != null)
                SalesGrid.SelectedItem = first;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}

internal sealed class SaleSummaryRow
{
    private static readonly CultureInfo BrazilianCulture = CultureInfo.GetCultureInfo("pt-BR");

    public SaleSummaryRow(PdvSaleSummary sale)
    {
        SaleId = sale.SaleId;
        SaleNumber = sale.SaleNumber;
        TimeText = sale.CompletedAtUtc.LocalDateTime.ToString("HH:mm", BrazilianCulture);
        ItemCount = sale.ItemCount;
        TotalText = sale.TotalAmount.ToString("C", BrazilianCulture);
        PaymentMethods = sale.PaymentMethods;
        IsCompleted = sale.Status == "completed";
        IsCancelled = sale.Status == "cancelled";

        (SyncLabel, SyncColor) = sale.Status == "cancelled"
            ? ("Cancelado", new SolidColorBrush(Color.FromRgb(100, 116, 139)))
            : sale.SyncStatus switch
            {
                "accepted"     => ("Sincronizado", new SolidColorBrush(Color.FromRgb(22, 101, 52))),
                "sent"         => ("Enviado",      new SolidColorBrush(Color.FromRgb(30, 64, 175))),
                "pending_sync" => ("Pendente",     new SolidColorBrush(Color.FromRgb(146, 64, 14))),
                "rejected"     => ("Rejeitado",    new SolidColorBrush(Color.FromRgb(185, 28, 28))),
                _              => (sale.SyncStatus, new SolidColorBrush(Color.FromRgb(100, 116, 139)))
            };
    }

    public Guid SaleId { get; }
    public string SaleNumber { get; }
    public string TimeText { get; }
    public int ItemCount { get; }
    public string TotalText { get; }
    public string PaymentMethods { get; }
    public bool IsCompleted { get; }
    public bool IsCancelled { get; }
    public string SyncLabel { get; }
    public SolidColorBrush SyncColor { get; }
}

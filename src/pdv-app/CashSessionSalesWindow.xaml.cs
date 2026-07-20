using System.Globalization;
using System.Windows;
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
    private bool _openingDetail;

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

    private async void SalesGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // Clicar/selecionar a venda abre o detalhe (itens, pagamentos,
        // reimpressao e cancelamento). Guard evita reentrancia quando a
        // selecao e limpa apos fechar o dialogo.
        if (_openingDetail || SalesGrid.SelectedItem is not SaleSummaryRow row)
        {
            return;
        }

        _openingDetail = true;
        try
        {
            var detailWindow = new SaleDetailWindow(
                _saleRepository,
                _operatorRepository,
                _currentOperator,
                _cashSessionId,
                row.SaleId)
            { Owner = this };
            detailWindow.ShowDialog();

            SalesGrid.SelectedItem = null;
            if (detailWindow.SaleChanged)
            {
                await LoadSalesAsync();
            }
        }
        finally
        {
            _openingDetail = false;
        }
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

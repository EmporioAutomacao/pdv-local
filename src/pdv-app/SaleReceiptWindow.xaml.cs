using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using PdvLocal.Core;

namespace PdvLocal.App;

public partial class SaleReceiptWindow : Window
{
    private static readonly CultureInfo BrazilianCulture = CultureInfo.GetCultureInfo("pt-BR");

    private readonly SaleReceiptData _receipt;
    private readonly PdvSaleRepository _saleRepository;
    private readonly DispatcherTimer _syncTimer;

    internal SaleReceiptWindow(SaleReceiptData receipt, PdvSaleRepository saleRepository)
    {
        _receipt = receipt;
        _saleRepository = saleRepository;
        _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _syncTimer.Tick += async (_, _) => await CheckSyncStatusAsync();
        InitializeComponent();
        Populate();
        _syncTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _syncTimer.Stop();
        base.OnClosed(e);
    }

    private async Task CheckSyncStatusAsync()
    {
        try
        {
            var status = await _saleRepository.GetSyncStatusAsync(_receipt.SaleId, CancellationToken.None);
            switch (status)
            {
                case "accepted":
                    SyncStatusText.Text = "Sincronizado com o ERP.";
                    SyncStatusText.Foreground = System.Windows.Media.Brushes.DarkGreen;
                    _syncTimer.Stop();
                    break;
                case "rejected":
                    SyncStatusText.Text = "Rejeitado pelo ERP. Verifique o SyncAgent.";
                    SyncStatusText.Foreground = System.Windows.Media.Brushes.DarkRed;
                    _syncTimer.Stop();
                    break;
                case "sent":
                    SyncStatusText.Text = "Enviado ao ERP. Aguardando confirmacao...";
                    break;
            }
        }
        catch
        {
            // silencioso — não interrompe a UX do comprovante
        }
    }

    private void Populate()
    {
        SaleNumberText.Text = _receipt.SaleNumber;
        DateText.Text = _receipt.CompletedAt.LocalDateTime.ToString("dd/MM/yyyy HH:mm:ss", BrazilianCulture);
        OperatorText.Text = $"Operador: {_receipt.OperatorName}";

        ItemsList.ItemsSource = _receipt.Items
            .Select(item => new ReceiptItemRow(item))
            .ToList();

        SubtotalText.Text = FormatMoney(_receipt.SubtotalAmount);

        ItemDiscountRow.Visibility = _receipt.ItemDiscountAmount > 0 ? Visibility.Visible : Visibility.Collapsed;
        ItemDiscountText.Text = $"- {FormatMoney(_receipt.ItemDiscountAmount)}";

        SaleDiscountRow.Visibility = _receipt.SaleDiscountAmount > 0 ? Visibility.Visible : Visibility.Collapsed;
        SaleDiscountText.Text = $"- {FormatMoney(_receipt.SaleDiscountAmount)}";

        TotalText.Text = FormatMoney(_receipt.TotalAmount);

        PaymentsList.ItemsSource = _receipt.Payments
            .Select(p => new ReceiptPaymentRow(p))
            .ToList();

        ChangeRow.Visibility = _receipt.TotalChangeAmount > 0 ? Visibility.Visible : Visibility.Collapsed;
        ChangeText.Text = FormatMoney(_receipt.TotalChangeAmount);

        SyncStatusText.Text = "Aguardando sincronizacao com ERP...";
    }

    private void PrintButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new System.Windows.Controls.PrintDialog();
        if (dialog.ShowDialog() == true)
        {
            dialog.PrintVisual(ReceiptPanel, _receipt.SaleNumber);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private static string FormatMoney(decimal value) =>
        value.ToString("C", BrazilianCulture);
}

internal sealed class ReceiptItemRow
{
    private static readonly CultureInfo BrazilianCulture = CultureInfo.GetCultureInfo("pt-BR");

    public ReceiptItemRow(UiSaleItem item)
    {
        Name = item.Name;
        QuantityText = string.IsNullOrWhiteSpace(item.UnitLabel)
            ? item.Quantity.ToString("0.####", BrazilianCulture)
            : $"{item.Quantity.ToString("0.####", BrazilianCulture)} {item.UnitLabel}";
        UnitPriceText = item.UnitPrice.ToString("C", BrazilianCulture);
        TotalAmountText = item.TotalAmount.ToString("C", BrazilianCulture);
        HasDiscount = item.DiscountAmount > 0 ? Visibility.Visible : Visibility.Collapsed;
        DiscountLine = item.DiscountAmount > 0
            ? $"Desc: - {item.DiscountAmount.ToString("C", BrazilianCulture)}"
            : string.Empty;
    }

    public string Name { get; }
    public string QuantityText { get; }
    public string UnitPriceText { get; }
    public string TotalAmountText { get; }
    public Visibility HasDiscount { get; }
    public string DiscountLine { get; }
}

internal sealed class ReceiptPaymentRow
{
    private static readonly CultureInfo BrazilianCulture = CultureInfo.GetCultureInfo("pt-BR");

    public ReceiptPaymentRow(UiPayment payment)
    {
        var condition = payment.Installments > 1
            ? $" {payment.Installments}x"
            : string.Empty;
        Label = $"{payment.Method}{condition}";
        AmountText = payment.Amount.ToString("C", BrazilianCulture);
    }

    public string Label { get; }
    public string AmountText { get; }
}

using System.Globalization;
using System.Text.Json;
using System.Windows;
using PdvLocal.Core;
using static PdvLocal.App.PdvUiFormatting;

namespace PdvLocal.App;

public partial class SaleDetailWindow : Window
{
    private static readonly CultureInfo BrazilianCulture = CultureInfo.GetCultureInfo("pt-BR");

    private readonly PdvSaleRepository _saleRepository;
    private readonly PdvOperatorRepository _operatorRepository;
    private readonly PdvOperator _currentOperator;
    private readonly Guid _cashSessionId;
    private readonly Guid _saleId;
    private PdvSaleDetail? _detail;

    public bool SaleChanged { get; private set; }

    internal SaleDetailWindow(
        PdvSaleRepository saleRepository,
        PdvOperatorRepository operatorRepository,
        PdvOperator currentOperator,
        Guid cashSessionId,
        Guid saleId)
    {
        _saleRepository = saleRepository;
        _operatorRepository = operatorRepository;
        _currentOperator = currentOperator;
        _cashSessionId = cashSessionId;
        _saleId = saleId;
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadDetailAsync();
    }

    private async Task LoadDetailAsync()
    {
        try
        {
            _detail = await _saleRepository.GetCompletedSaleDetailAsync(_saleId, CancellationToken.None);
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException or TimeoutException)
        {
            ShowMessage($"Erro ao carregar a venda: {ex.Message}");
            return;
        }

        if (_detail is null)
        {
            ShowMessage("Venda nao encontrada.");
            ReprintSaleButton.IsEnabled = false;
            CancelSaleButton.Visibility = Visibility.Collapsed;
            return;
        }

        var detail = _detail;
        SaleNumberText.Text = detail.SaleNumber;
        var isCancelled = detail.Status == "cancelled";
        SaleStatusText.Text = isCancelled ? "CANCELADA" : "Finalizada";
        SaleStatusText.Foreground = isCancelled
            ? BrushFromHex("#B91C1C")
            : BrushFromHex("#166534");
        CancelSaleButton.Visibility = isCancelled ? Visibility.Collapsed : Visibility.Visible;

        var completedText = detail.CompletedAtUtc?.LocalDateTime.ToString("dd/MM/yyyy HH:mm", BrazilianCulture) ?? "-";
        var customerText = detail.CustomerName is not null
            ? $"Cliente: {detail.CustomerName}"
            : detail.CustomerDocument is not null
                ? $"CPF/CNPJ: {detail.CustomerDocument}"
                : "Consumidor";
        SaleInfoText.Text = $"Operador: {detail.OperatorName}   {customerText}   Finalizada em {completedText}"
            + (detail.CancelledAtUtc is { } cancelledAt
                ? $"   Cancelada em {cancelledAt.LocalDateTime:dd/MM/yyyy HH:mm}"
                : string.Empty);

        ItemsGrid.ItemsSource = detail.Items.Select(BuildUiSaleItem).ToList();
        PaymentsGrid.ItemsSource = detail.Payments.Select(payment => new SaleDetailPaymentRow(payment)).ToList();
        TotalsText.Text = $"Total: {FormatMoney(detail.TotalAmount)}"
            + (detail.DiscountAmount > 0 ? $"   (descontos: {FormatMoney(detail.DiscountAmount)})" : string.Empty);
    }

    private static UiSaleItem BuildUiSaleItem(PdvSaleDetailItem item)
    {
        return new UiSaleItem
        {
            ProductId = item.ProductId,
            ExternalKey = item.ExternalKey,
            Sku = item.Sku,
            Barcode = item.Barcode,
            Name = item.Name,
            LineNumber = item.LineNumber,
            Quantity = item.Quantity,
            UnitPrice = item.UnitPrice,
            DiscountAmount = item.DiscountAmount,
            UnitLabel = item.UnitLabel ?? item.Unit
        };
    }

    private void ReprintSaleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_detail is null)
        {
            return;
        }

        var items = _detail.Items.Select(BuildUiSaleItem).ToList();
        var itemDiscount = _detail.Items.Sum(item => item.DiscountAmount);
        var saleDiscount = Math.Max(0, _detail.DiscountAmount - itemDiscount);
        var payments = _detail.Payments
            .Select(payment => new UiPayment
            {
                Method = payment.PaymentMethod,
                Amount = payment.Amount,
                ReceivedAmount = payment.Amount,
                Installments = payment.Installments,
                AuthorizationCode = payment.AuthorizationCode
            })
            .ToList();

        // Troco/valor recebido nao sao persistidos: a reimpressao omite troco.
        var receipt = new SaleReceiptData(
            _detail.SaleId,
            _detail.SaleNumber,
            _detail.CompletedAtUtc ?? DateTimeOffset.Now,
            _detail.OperatorName,
            items,
            _detail.SubtotalAmount,
            itemDiscount,
            saleDiscount,
            _detail.TotalAmount,
            payments,
            TotalChangeAmount: 0);

        new SaleReceiptWindow(receipt, _saleRepository) { Owner = this }.Show();
    }

    private async void CancelSaleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_detail is null || _detail.Status != "completed")
        {
            return;
        }

        var authorization = SupervisorAuthorizationDialog.Request(
            this, _operatorRepository, $"cancelar venda {_detail.SaleNumber}");
        if (authorization is null)
        {
            return;
        }

        try
        {
            await _saleRepository.CancelSaleAsync(
                _detail.SaleId,
                _cashSessionId,
                _currentOperator.OperatorId,
                authorization.Supervisor.OperatorId,
                authorization.Reason,
                CancellationToken.None);
            SaleChanged = true;
            await LoadDetailAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Npgsql.NpgsqlException or TimeoutException)
        {
            ShowMessage(ex.Message);
        }
    }

    private void ShowMessage(string message)
    {
        DetailMessageText.Text = message;
        DetailMessageText.Visibility = Visibility.Visible;
    }
}

internal sealed class SaleDetailPaymentRow
{
    private static readonly CultureInfo BrazilianCulture = CultureInfo.GetCultureInfo("pt-BR");

    public SaleDetailPaymentRow(PdvSaleDetailPayment payment)
    {
        Method = payment.PaymentMethod;
        AmountText = payment.Amount.ToString("C", BrazilianCulture);
        InstallmentsText = FormatInstallments(payment);
    }

    public string Method { get; }
    public string AmountText { get; }
    public string InstallmentsText { get; }

    private static string FormatInstallments(PdvSaleDetailPayment payment)
    {
        if (string.IsNullOrWhiteSpace(payment.InstallmentsPlanJson))
        {
            return payment.Installments is > 1 ? $"{payment.Installments}x" : "A vista";
        }

        try
        {
            using var document = JsonDocument.Parse(payment.InstallmentsPlanJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return payment.Installments is > 1 ? $"{payment.Installments}x" : "A vista";
            }

            var parts = new List<string>();
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                var dueDateText = entry.TryGetProperty("due_date", out var dueDate) ? dueDate.GetString() : null;
                var amount = entry.TryGetProperty("amount", out var amountElement) && amountElement.ValueKind == JsonValueKind.Number
                    ? amountElement.GetDecimal()
                    : 0;
                var formattedDate = DateOnly.TryParse(dueDateText, out var parsed)
                    ? parsed.ToString("dd/MM", BrazilianCulture)
                    : dueDateText ?? "-";
                parts.Add($"{formattedDate} {amount.ToString("C", BrazilianCulture)}");
            }

            return parts.Count == 0
                ? "A vista"
                : $"{parts.Count}x: {string.Join("; ", parts)}";
        }
        catch (JsonException)
        {
            return payment.Installments is > 1 ? $"{payment.Installments}x" : "A vista";
        }
    }
}

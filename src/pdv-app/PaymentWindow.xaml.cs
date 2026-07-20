using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using PdvLocal.Core;
using static PdvLocal.App.PdvUiFormatting;

namespace PdvLocal.App;

public partial class PaymentWindow : Window
{
    private readonly decimal _subtotal;
    private readonly decimal _itemDiscountTotal;
    private readonly ObservableCollection<UiPayment> _payments;
    private readonly ObservableCollection<UiPaymentSpecies> _paymentSpecies;
    private readonly ObservableCollection<UiPaymentCondition> _paymentConditions;
    private readonly ITefPaymentProvider _tefPaymentProvider;
    private readonly string _tefProviderName;
    private readonly PdvOperatorRepository _operatorRepository;
    private readonly PdvCustomerRepository _customerRepository;
    private readonly Func<string, SupervisorAuthorization, string, Task> _recordAuditAsync;

    private bool _resolvingCustomer;

    public decimal SaleDiscount { get; private set; }
    public string? CustomerDocument { get; private set; }
    public Guid? CustomerId { get; private set; }

    internal PaymentWindow(
        decimal subtotal,
        decimal itemDiscountTotal,
        decimal initialSaleDiscount,
        ObservableCollection<UiPayment> payments,
        ObservableCollection<UiPaymentSpecies> paymentSpecies,
        ObservableCollection<UiPaymentCondition> paymentConditions,
        ITefPaymentProvider tefPaymentProvider,
        string tefProviderName,
        PdvOperatorRepository operatorRepository,
        PdvCustomerRepository customerRepository,
        Func<string, SupervisorAuthorization, string, Task> recordAuditAsync)
    {
        _subtotal = subtotal;
        _itemDiscountTotal = itemDiscountTotal;
        SaleDiscount = initialSaleDiscount;
        _payments = payments;
        _paymentSpecies = paymentSpecies;
        _paymentConditions = paymentConditions;
        _tefPaymentProvider = tefPaymentProvider;
        _tefProviderName = tefProviderName;
        _operatorRepository = operatorRepository;
        _customerRepository = customerRepository;
        _recordAuditAsync = recordAuditAsync;

        InitializeComponent();
        PaymentsGrid.ItemsSource = _payments;
        PaymentMethodComboBox.ItemsSource = _paymentSpecies;
        PaymentConditionComboBox.ItemsSource = _paymentConditions;
        SaleDiscountTextBox.Text = FormatDecimal(initialSaleDiscount);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        SelectDefaultPaymentCatalogItems();
        UpdateTotals();
        PaymentReceivedTextBox.Focus();
        PaymentReceivedTextBox.SelectAll();
    }

    private void PaymentWindow_KeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.F10 or Key.F12)
        {
            e.Handled = true;
            CompleteSaleButton_Click(CompleteSaleButton, e);
        }
    }

    private void SelectDefaultPaymentCatalogItems()
    {
        if (PaymentMethodComboBox.SelectedItem is not UiPaymentSpecies && _paymentSpecies.Count > 0)
        {
            PaymentMethodComboBox.SelectedItem = _paymentSpecies.FirstOrDefault(item => item.Kind == "cash")
                ?? _paymentSpecies[0];
        }

        if (PaymentConditionComboBox.SelectedItem is not UiPaymentCondition && _paymentConditions.Count > 0)
        {
            PaymentConditionComboBox.SelectedItem = _paymentConditions.FirstOrDefault(item => item.Installments == 1 && item.FirstDueDays == 0)
                ?? _paymentConditions[0];
        }
    }

    private void SaleDiscountTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        SaleDiscount = TryParseMoney(SaleDiscountTextBox.Text, out var discount) ? discount : 0;
        if (SaleDiscount > 0)
        {
            SaleDiscountTextBox.Background = BrushFromHex("#FEF3C7");
            SaleDiscountTextBox.Foreground = BrushFromHex("#92400E");
        }
        else
        {
            SaleDiscountTextBox.SetResourceReference(BackgroundProperty, "InputBg");
            SaleDiscountTextBox.SetResourceReference(ForegroundProperty, "InputFg");
        }

        UpdateTotals();
    }

    private void PaymentReceivedTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        AddPaymentButton_Click(AddPaymentButton, e);
    }

    private void PaymentReceivedTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (TryParseMoney(PaymentReceivedTextBox.Text, out var current) && current != 0)
            return;
        try
        {
            var remaining = CalculateRemainingAmount();
            if (remaining > 0)
            {
                PaymentReceivedTextBox.Text = FormatDecimal(remaining);
                PaymentReceivedTextBox.SelectAll();
            }
        }
        catch (ArgumentException)
        {
            // Desconto invalido, nao preenche automaticamente
        }
    }

    private void PaymentsGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete)
            return;
        e.Handled = true;
        RemovePaymentButton_Click(RemovePaymentButton, e);
    }

    private async void AddPaymentButton_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            var remaining = CalculateRemainingAmount();
            if (remaining <= 0)
            {
                throw new InvalidOperationException("A venda ja esta paga.");
            }

            var method = ReadPaymentMethod();
            var condition = ReadPaymentCondition();
            var receivedAmount = ParseMoney(PaymentReceivedTextBox.Text, "Valor recebido");
            if (receivedAmount <= 0)
            {
                throw new ArgumentException("Valor recebido deve ser maior que zero.");
            }

            var requiresRegisteredCustomer = PdvValidation.RequiresRegisteredCustomer(
                condition.Installments, condition.FirstDueDays);
            if (requiresRegisteredCustomer)
            {
                await ResolveCustomerDocumentAsync();
                if (CustomerId is null)
                {
                    throw new InvalidOperationException(
                        "Venda a prazo exige cliente cadastrado. Informe o codigo do cliente ou use a lupa para selecionar.");
                }
            }

            var isCash = method.AllowsChange || IsCashPayment(method.Name);
            var isTef = method.RequiresTef || IsTefPayment(method.Name);
            var appliedAmount = receivedAmount;
            var changeAmount = 0m;
            TefAuthorizationResult? tefAuthorization = null;

            if (receivedAmount > remaining)
            {
                if (!isCash)
                {
                    throw new ArgumentException("Pagamento maior que o restante so e permitido para dinheiro, por causa do troco.");
                }

                appliedAmount = remaining;
                changeAmount = receivedAmount - remaining;
            }

            if (isTef)
            {
                tefAuthorization = await _tefPaymentProvider.AuthorizeAsync(
                    new TefAuthorizationRequest(
                        method.Name,
                        appliedAmount,
                        condition.Installments,
                        condition.Name,
                        method.ExternalKey,
                        condition.ExternalKey),
                    CancellationToken.None);
            }

            var payment = new UiPayment
            {
                Method = method.Name,
                Condition = condition.Name,
                Amount = appliedAmount,
                ReceivedAmount = receivedAmount,
                ChangeAmount = changeAmount,
                AuthorizationCode = tefAuthorization?.AuthorizationCode,
                PaymentSpeciesId = method.PaymentSpeciesId,
                PaymentSpeciesExternalKey = method.ExternalKey,
                PaymentSpeciesKind = method.Kind,
                PaymentConditionId = condition.PaymentConditionId,
                PaymentConditionExternalKey = condition.ExternalKey,
                Installments = condition.Installments,
                RequiresTef = isTef,
                AllowsChange = method.AllowsChange,
                TefMetadataJson = tefAuthorization?.MetadataJson,
                RequiresRegisteredCustomer = requiresRegisteredCustomer
            };

            if (requiresRegisteredCustomer)
            {
                payment.InstallmentsPlan = PdvInstallmentCalculator.BuildPlan(
                    appliedAmount,
                    condition.Installments,
                    condition.FirstDueDays,
                    condition.IntervalDays,
                    DateOnly.FromDateTime(DateTime.Today));
            }

            _payments.Add(payment);
            PaymentsGrid.SelectedItem = payment;

            PaymentReceivedTextBox.Text = FormatDecimal(CalculateRemainingAmount());
            UpdateTotals();
            ShowMessage(isTef
                ? $"Pagamento TEF {tefAuthorization?.Status ?? "capturado"} registrado pelo provider {_tefProviderName}."
                : payment.InstallmentsPlan is not null
                    ? "Pagamento a prazo registrado. Confira as datas das parcelas abaixo (editaveis)."
                    : "Pagamento registrado.",
                isError: false);
        });
    }

    private UiPayment? _installmentsPaymentBeingEdited;

    private void PaymentsGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        SaveDisplayedInstallmentEdits();
        ShowInstallmentsForSelectedPayment();
    }

    private void SaveDisplayedInstallmentEdits()
    {
        if (_installmentsPaymentBeingEdited is not { InstallmentsPlan: not null } payment
            || InstallmentsGrid.ItemsSource is not IReadOnlyList<UiInstallmentRow> rows)
        {
            return;
        }

        InstallmentsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
        try
        {
            payment.InstallmentsPlan = ParseInstallmentRows(rows);
        }
        catch (ArgumentException)
        {
            // Data invalida digitada: mantem o plano anterior; a validacao
            // definitiva acontece ao concluir a venda.
        }
    }

    private void ShowInstallmentsForSelectedPayment()
    {
        if (PaymentsGrid.SelectedItem is not UiPayment { InstallmentsPlan: { Count: > 0 } plan } selected)
        {
            _installmentsPaymentBeingEdited = null;
            InstallmentsPanel.Visibility = Visibility.Collapsed;
            InstallmentsGrid.ItemsSource = null;
            return;
        }

        _installmentsPaymentBeingEdited = selected;
        InstallmentsGrid.ItemsSource = plan
            .Select(entry => new UiInstallmentRow
            {
                Number = entry.Number,
                DueDateText = entry.DueDate.ToString("dd/MM/yyyy"),
                Amount = entry.Amount
            })
            .ToList();
        InstallmentsPanel.Visibility = Visibility.Visible;
    }

    private void ApplyEditedInstallmentPlans()
    {
        // Persiste (com validacao estrita de formato) o plano em edicao.
        if (_installmentsPaymentBeingEdited is { InstallmentsPlan: not null } editing
            && InstallmentsGrid.ItemsSource is IReadOnlyList<UiInstallmentRow> rows)
        {
            InstallmentsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
            editing.InstallmentsPlan = ParseInstallmentRows(rows);
        }

        foreach (var payment in _payments)
        {
            if (payment.InstallmentsPlan is { Count: > 0 } plan)
            {
                PdvInstallmentCalculator.ValidatePlan(plan, payment.Amount);
            }
        }
    }

    private static IReadOnlyList<PdvInstallmentPlanEntry> ParseInstallmentRows(IReadOnlyList<UiInstallmentRow> rows)
    {
        var entries = new List<PdvInstallmentPlanEntry>(rows.Count);
        foreach (var row in rows)
        {
            if (!DateOnly.TryParseExact(row.DueDateText.Trim(), "dd/MM/yyyy", out var dueDate))
            {
                throw new ArgumentException(
                    $"Vencimento da parcela {row.Number} invalido: use o formato dd/mm/aaaa.");
            }

            entries.Add(new PdvInstallmentPlanEntry(row.Number, dueDate, row.Amount));
        }

        return entries;
    }

    private async void RemovePaymentButton_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            if (PaymentsGrid.SelectedItem is not UiPayment selected)
            {
                throw new InvalidOperationException("Selecione um pagamento para remover.");
            }

            var authorization = SupervisorAuthorizationDialog.Request(this, _operatorRepository, "remover pagamento")
                ?? throw new InvalidOperationException("Autorizacao de supervisor cancelada para remover pagamento.");
            await _recordAuditAsync("payment_removed", authorization, BuildPaymentPayload(selected));

            _payments.Remove(selected);
            UpdateTotals();
            ShowMessage("Pagamento removido com autorizacao de supervisor.", isError: false);
        });
    }

    private async void CustomerDocumentTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            await ResolveCustomerDocumentAsync();
        });
    }

    private async void CustomerDocumentTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        await RunAsync(async () =>
        {
            await ResolveCustomerDocumentAsync();
        });
    }

    private void CustomerSearchButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new CustomerSearchWindow(_customerRepository) { Owner = this };
        if (window.ShowDialog() != true || window.SelectedCustomer is not { } customer)
        {
            return;
        }

        ApplyResolvedCustomer(customer);
        CustomerDocumentTextBox.Text = customer.Document ?? customer.ExternalKey ?? string.Empty;
        HideMessage();
    }

    private async Task ResolveCustomerDocumentAsync()
    {
        if (_resolvingCustomer)
        {
            return;
        }

        _resolvingCustomer = true;
        try
        {
            var inputText = CustomerDocumentTextBox.Text.Trim();
            if (inputText.Length == 0)
            {
                CustomerDocument = null;
                CustomerId = null;
                CustomerNameText.Text = "";
                return;
            }

            switch (PdvValidation.ClassifyCustomerLookupInput(inputText))
            {
                case CustomerLookupInputKind.Document:
                    if (!PdvValidation.TryNormalizeCustomerDocument(inputText, out var normalized))
                    {
                        CustomerDocument = null;
                        CustomerId = null;
                        CustomerNameText.Text = "";
                        throw new ArgumentException("CPF/CNPJ invalido.");
                    }

                    CustomerDocument = normalized;
                    var byDocument = await _customerRepository.FindActiveByDocumentAsync(normalized, CancellationToken.None);
                    CustomerId = byDocument?.CustomerId;
                    CustomerNameText.Text = byDocument is null
                        ? "Consumidor nao cadastrado"
                        : $"Cliente: {byDocument.Name}";
                    break;

                case CustomerLookupInputKind.InternalCode:
                    var byCode = await _customerRepository.FindActiveByCodeAsync(inputText, CancellationToken.None)
                        ?? throw new ArgumentException($"Cliente com codigo {inputText} nao encontrado.");
                    ApplyResolvedCustomer(byCode);
                    break;

                default:
                    CustomerDocument = null;
                    CustomerId = null;
                    CustomerNameText.Text = "";
                    throw new ArgumentException("Informe CPF/CNPJ ou o codigo interno do cliente.");
            }
        }
        finally
        {
            _resolvingCustomer = false;
        }
    }

    private void ApplyResolvedCustomer(PdvCustomer customer)
    {
        CustomerId = customer.CustomerId;
        CustomerDocument = customer.Document is { } document
            && PdvValidation.TryNormalizeCustomerDocument(document, out var digits)
            ? digits
            : null;
        CustomerNameText.Text = $"Cliente: {customer.Name}";
    }

    private async void CompleteSaleButton_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            var remaining = CalculateRemainingAmount();
            if (remaining > 0)
            {
                throw new InvalidOperationException($"Ainda restam {FormatMoney(remaining)} a pagar.");
            }

            await ResolveCustomerDocumentAsync();

            if (_payments.Any(payment => payment.RequiresRegisteredCustomer) && CustomerId is null)
            {
                throw new InvalidOperationException(
                    "Venda a prazo exige cliente cadastrado. Informe o codigo do cliente ou use a lupa para selecionar.");
            }

            ApplyEditedInstallmentPlans();
            DialogResult = true;
        });
    }

    private decimal CalculateRemainingAmount()
    {
        var saleDiscount = ParseMoney(SaleDiscountTextBox.Text, "Desconto total");
        var total = _subtotal - _itemDiscountTotal - saleDiscount;
        var paid = _payments.Sum(payment => payment.Amount);
        if (total < 0)
        {
            throw new ArgumentException("Desconto total nao pode deixar a venda negativa.");
        }

        return Math.Max(0, total - paid);
    }

    private void UpdateTotals()
    {
        if (TotalValue is null || PaidValue is null || RemainingValue is null || ChangeValue is null)
        {
            return;
        }

        var total = Math.Max(0, _subtotal - _itemDiscountTotal - SaleDiscount);
        var paid = _payments.Sum(payment => payment.Amount);
        var remaining = Math.Max(0, total - paid);
        var change = _payments.Sum(payment => payment.ChangeAmount);

        TotalValue.Text = FormatMoney(total);
        PaidValue.Text = FormatMoney(paid);
        RemainingValue.Text = FormatMoney(remaining);
        if (remaining > 0)
        {
            RemainingValue.Foreground = BrushFromHex("#DC2626");
        }
        else
        {
            RemainingValue.SetResourceReference(ForegroundProperty, "TextPrimary");
        }

        ChangeValue.Text = FormatMoney(change);
    }

    private UiPaymentSpecies ReadPaymentMethod()
    {
        if (PaymentMethodComboBox.SelectedItem is UiPaymentSpecies selected)
        {
            return selected;
        }

        var methodName = PaymentMethodComboBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(methodName))
        {
            throw new ArgumentException("Especie de pagamento e obrigatoria.");
        }

        return _paymentSpecies.FirstOrDefault(item => item.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase))
            ?? UiPaymentSpecies.FromTypedName(methodName);
    }

    private UiPaymentCondition ReadPaymentCondition()
    {
        if (PaymentConditionComboBox.SelectedItem is UiPaymentCondition selected)
        {
            return selected;
        }

        var conditionName = PaymentConditionComboBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(conditionName))
        {
            throw new ArgumentException("Condicao de pagamento e obrigatoria.");
        }

        return _paymentConditions.FirstOrDefault(item => item.Name.Equals(conditionName, StringComparison.OrdinalIgnoreCase))
            ?? new UiPaymentCondition(null, $"typed-{conditionName}", conditionName, 1, 0, 0);
    }

    private static bool IsCashPayment(string method)
    {
        return method.Contains("dinheiro", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTefPayment(string method)
    {
        return method.Contains("tef", StringComparison.OrdinalIgnoreCase)
            || method.Contains("cartao", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildPaymentPayload(UiPayment payment)
    {
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            payment.Method,
            payment.Condition,
            payment.Amount,
            payment.ReceivedAmount,
            payment.ChangeAmount,
            payment.AuthorizationCode,
            payment.PaymentSpeciesExternalKey,
            payment.PaymentSpeciesKind,
            payment.PaymentConditionExternalKey,
            payment.Installments,
            payment.RequiresTef,
            payment.AllowsChange,
            payment.TefMetadataJson
        });
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

    private void ShowMessage(string message, bool isError)
    {
        PaymentMessageText.Text = message;
        if (isError)
        {
            PaymentMessageText.Foreground = BrushFromHex("#E53E3E");
        }
        else
        {
            PaymentMessageText.SetResourceReference(ForegroundProperty, "SuccessText");
        }

        PaymentMessageText.Visibility = Visibility.Visible;
    }

    private void HideMessage()
    {
        PaymentMessageText.Visibility = Visibility.Collapsed;
    }
}

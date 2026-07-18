using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PdvLocal.Core;
using static PdvLocal.App.PdvUiFormatting;

namespace PdvLocal.App;

public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions AuditJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly PdvAppConfiguration _configuration;
    private readonly PdvDatabaseStatusReader _databaseStatusReader;
    private readonly PdvOperatorRepository _operatorRepository;
    private readonly PdvCashSessionRepository _cashSessionRepository;
    private readonly PdvProductRepository _productRepository;
    private readonly PdvPaymentCatalogRepository _paymentCatalogRepository;
    private readonly PdvSaleRepository _saleRepository;
    private readonly PdvOperationAuditRepository _operationAuditRepository;
    private readonly PdvCashMovementRepository _cashMovementRepository;
    private readonly ITefPaymentProvider _tefPaymentProvider;
    private readonly HttpClient _httpClient;
    private readonly DispatcherTimer _statusRefreshTimer;
    private bool _isRefreshingStatus;
    private readonly ObservableCollection<UiProductSearchResult> _productSearchResults = [];
    private readonly ObservableCollection<UiSaleItem> _saleItems = [];
    private readonly ObservableCollection<UiPayment> _payments = [];
    private readonly ObservableCollection<UiPaymentSpecies> _paymentSpecies = [];
    private readonly ObservableCollection<UiPaymentCondition> _paymentConditions = [];
    private PdvOperator? _currentOperator;
    private PdvCashSession? _currentCashSession;
    private PdvProduct? _selectedProduct;
    private SaleReceiptData? _lastReceipt;
    private PdvOperator? _initialOperator;

    public MainWindow(PdvOperator initialOperator) : this()
    {
        _initialOperator = initialOperator;
    }

    public MainWindow()
    {
        _configuration = PdvAppConfiguration.Load();
        _databaseStatusReader = new PdvDatabaseStatusReader(_configuration.PdvLocalConnectionString);
        _operatorRepository = new PdvOperatorRepository(_configuration.PdvLocalConnectionString);
        _cashSessionRepository = new PdvCashSessionRepository(_configuration.PdvLocalConnectionString);
        _productRepository = new PdvProductRepository(_configuration.PdvLocalConnectionString);
        _paymentCatalogRepository = new PdvPaymentCatalogRepository(_configuration.PdvLocalConnectionString);
        _saleRepository = new PdvSaleRepository(_configuration.PdvLocalConnectionString);
        _operationAuditRepository = new PdvOperationAuditRepository(_configuration.PdvLocalConnectionString);
        _cashMovementRepository = new PdvCashMovementRepository(_configuration.PdvLocalConnectionString);
        _tefPaymentProvider = new ConfiguredTefPaymentProvider(_configuration.Tef);
        _httpClient = new HttpClient
        {
            BaseAddress = _configuration.SyncAgentLocalApiBaseUrl,
            Timeout = TimeSpan.FromSeconds(5)
        };
        _statusRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _statusRefreshTimer.Tick += async (_, _) => await RefreshStatusAsync();

        InitializeComponent();
        ProductSearchResultsGrid.ItemsSource = _productSearchResults;
        CartItemsGrid.ItemsSource = _saleItems;
        PaymentsGrid.ItemsSource = _payments;
        PaymentMethodComboBox.ItemsSource = _paymentSpecies;
        PaymentConditionComboBox.ItemsSource = _paymentConditions;
        LocalApiValue.Text = _configuration.SyncAgentLocalApiBaseUrl.ToString();
        ResetSale(clearMessage: false);
        ApplyCurrentSession();
    }

    protected override void OnClosed(EventArgs e)
    {
        _statusRefreshTimer.Stop();
        _httpClient.Dispose();
        base.OnClosed(e);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        AppVersionValue.Text = typeof(MainWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "desconhecida";
        await _operationAuditRepository.EnsureSchemaAsync(CancellationToken.None);
        await _cashMovementRepository.EnsureSchemaAsync(CancellationToken.None);
        await RefreshStatusAsync();
        _statusRefreshTimer.Start();

        if (_initialOperator is not null)
        {
            OperatorLoginTextBox.Text = _initialOperator.Login;
            _currentOperator = _initialOperator;
            _currentCashSession = await _cashSessionRepository.FindOpenByOperatorAsync(
                _initialOperator.OperatorId, CancellationToken.None);
            ApplyCurrentSession();
            await RefreshCashSummaryAsync();
            SetOperationMessage($"Operador carregado: {_initialOperator.DisplayName}.", isError: false);
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshStatusAsync();
    }

    private void OpenSetupButton_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(new Uri(_configuration.SyncAgentLocalApiBaseUrl, "/setup").ToString())
        {
            UseShellExecute = true
        });
    }

    private async void LoadOperatorButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadOperatorAsync();
    }

    private async void OpenCashButton_Click(object sender, RoutedEventArgs e)
    {
        await RunPdvOperationAsync(async () =>
        {
            var operatorRecord = await LoadOperatorAsync();
            if (operatorRecord is null)
            {
                return;
            }

            var openingAmount = ParseMoney(OpeningAmountTextBox.Text, "Valor de abertura");
            var existingCashSession = await _cashSessionRepository.FindOpenByOperatorAsync(
                operatorRecord.OperatorId,
                CancellationToken.None);
            if (existingCashSession is not null)
            {
                _currentCashSession = existingCashSession;
                ApplyCurrentSession();
                await RefreshCashSummaryAsync();
                SetOperationMessage("Caixa aberto existente carregado.", isError: false);
                await LoadDraftIfAvailableAsync();
                FocusProductEntry();
                return;
            }

            var cashSessionId = await _cashSessionRepository.OpenAsync(
                new OpenCashSessionCommand(operatorRecord.OperatorId, openingAmount, null),
                CancellationToken.None);
            _currentCashSession = await _cashSessionRepository.FindOpenByOperatorAsync(
                operatorRecord.OperatorId,
                CancellationToken.None);
            ApplyCurrentSession();
            await RefreshCashSummaryAsync();
            SetOperationMessage($"Caixa aberto: {cashSessionId}.", isError: false);
            await LoadDraftIfAvailableAsync();
            FocusProductEntry();
            await RefreshDatabaseStatusAsync();
        });
    }

    private async void CloseCashButton_Click(object sender, RoutedEventArgs e)
    {
        await RunPdvOperationAsync(async () =>
        {
            EnsureCashSession();
            if (_saleItems.Count > 0 || _payments.Count > 0)
            {
                throw new InvalidOperationException("Finalize ou limpe a venda em andamento antes de fechar o caixa.");
            }

            var closingAmount = ParseMoney(ClosingAmountTextBox.Text, "Valor de fechamento");
            var summary = await _cashMovementRepository.GetSummaryAsync(
                _currentCashSession!.CashSessionId,
                CancellationToken.None);
            var difference = closingAmount - summary.ExpectedCashAmount;
            await _cashSessionRepository.CloseAsync(
                new CloseCashSessionCommand(
                    _currentCashSession.CashSessionId,
                    closingAmount,
                    BuildCloseCashNotes(summary, closingAmount)),
                CancellationToken.None);
            SetOperationMessage($"Caixa fechado: {_currentCashSession.CashSessionId}. Diferenca: {FormatMoney(difference)}.", isError: false);
            _currentCashSession = null;
            ApplyCurrentSession();
            await RefreshDatabaseStatusAsync();
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
        await RunPdvOperationAsync(async () =>
        {
            EnsureCashSession();
            if (_saleItems.Count > 0 || _payments.Count > 0)
            {
                throw new InvalidOperationException("Finalize ou limpe a venda em andamento antes de movimentar o caixa.");
            }

            var movementName = movementType == "supply" ? "suprimento" : "sangria";
            var amount = ParseMoney(CashMovementAmountTextBox.Text, $"Valor de {movementName}");
            var authorization = RequireSupervisorAuthorization($"registrar {movementName}");

            if (movementType == "withdrawal")
            {
                var summary = await _cashMovementRepository.GetSummaryAsync(
                    _currentCashSession!.CashSessionId,
                    CancellationToken.None);
                if (amount > summary.ExpectedCashAmount)
                {
                    throw new ArgumentException("Sangria nao pode ser maior que o dinheiro esperado no caixa.");
                }
            }

            var movementId = await _cashMovementRepository.InsertAsync(
                new PdvCashMovementCommand(
                    _currentCashSession!.CashSessionId,
                    _currentOperator!.OperatorId,
                    authorization.Supervisor.OperatorId,
                    movementType,
                    amount,
                    authorization.Reason),
                CancellationToken.None);

            CashMovementAmountTextBox.Text = "0,00";
            await RefreshCashSummaryAsync();
            SetOperationMessage($"{Capitalize(movementName)} registrado com auditoria. ID: {movementId}.", isError: false);
        });
    }

    private async void ProductSearchButton_Click(object sender, RoutedEventArgs e)
    {
        await RunPdvOperationAsync(async () =>
        {
            EnsureCashSession();
            await SearchProductsAsync();
        });
    }

    private async void ProductEntryTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await RunPdvOperationAsync(async () =>
        {
            EnsureCashSession();
            if (_selectedProduct is null)
            {
                await SearchProductsAsync();
            }

            if (_selectedProduct is not null)
            {
                AddSelectedProductToCart();
            }
        });
    }

    private void ProductSearchResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ProductSearchResultsGrid.SelectedItem is UiProductSearchResult selected)
        {
            SelectProduct(selected.Product);
        }
    }

    private async void AddItemButton_Click(object sender, RoutedEventArgs e)
    {
        await RunPdvOperationAsync(async () =>
        {
            EnsureCashSession();
            if (_selectedProduct is null)
            {
                await SearchProductsAsync();
            }

            AddSelectedProductToCart();
        });
    }

    private async void RemoveItemButton_Click(object sender, RoutedEventArgs e)
    {
        await RunPdvOperationAsync(async () =>
        {
            EnsureCashSession();
            if (CartItemsGrid.SelectedItem is not UiSaleItem selected)
            {
                throw new InvalidOperationException("Selecione um item para remover.");
            }

            var authorization = RequireSupervisorAuthorization("remover item da venda");
            await RecordDraftAuditAsync(
                "sale_item_removed",
                authorization,
                BuildSaleItemPayload(selected),
                CancellationToken.None);

            _saleItems.Remove(selected);
            RenumberSaleItems();
            UpdateTotals();
            SaveDraftToBackground();
            SetOperationMessage("Item removido da venda com autorizacao de supervisor.", isError: false);
        });
    }

    private async void NewSaleButton_Click(object sender, RoutedEventArgs e)
    {
        await RunPdvOperationAsync(async () =>
        {
            if (_saleItems.Count > 0 || _payments.Count > 0)
            {
                EnsureCashSession();
                var authorization = RequireSupervisorAuthorization("limpar venda em andamento");
                await RecordDraftAuditAsync(
                    "sale_draft_cancelled",
                    authorization,
                    BuildCurrentDraftPayload(),
                    CancellationToken.None);
            }

            ResetSale(clearMessage: true);
            FocusProductEntry();
        });
    }

    private void SaleInputTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        UpdateSelectedProductTotal();
    }

    private void SaleDiscountTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        UpdateTotals();
        var hasDiscount = TryParseMoney(SaleDiscountTextBox.Text, out var discount) && discount > 0;
        SaleDiscountTextBox.Background = hasDiscount ? BrushFromHex("#FEF3C7") : Brushes.White;
    }

    private void MainWindow_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F12:
                e.Handled = true;
                CompleteSaleButton_Click(CompleteSaleButton, e);
                break;
            case Key.F2:
                e.Handled = true;
                NewSaleButton_Click(NewSaleButton, e);
                break;
            case Key.F1:
                e.Handled = true;
                HelpButton_Click(HelpButton, e);
                break;
            case Key.F8 when CashSalesButton.IsEnabled:
                e.Handled = true;
                CashSalesButton_Click(CashSalesButton, e);
                break;
            case Key.F9 when _lastReceipt is not null:
                e.Handled = true;
                ReprintButton_Click(ReprintButton, e);
                break;
            case Key.Escape when _selectedProduct is not null:
                e.Handled = true;
                _selectedProduct = null;
                _productSearchResults.Clear();
                ProductEntryTextBox.Clear();
                UnitPriceTextBox.Text = "0,00";
                ItemDiscountTextBox.Text = "0,00";
                UpdateSelectedProductTotal();
                SetOperationMessage("Selecao cancelada.", isError: false);
                FocusProductEntry();
                break;
        }
    }

    private async void QuantityTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        await RunPdvOperationAsync(() =>
        {
            EnsureCashSession();
            AddSelectedProductToCart();
            return Task.CompletedTask;
        });
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
        if (_currentCashSession is null || _saleItems.Count == 0)
            return;
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

    private void ProductSearchResultsGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        if (ProductSearchResultsGrid.SelectedItem is UiProductSearchResult selected)
        {
            e.Handled = true;
            SelectProduct(selected.Product);
        }
    }

    private async void ItemDiscountTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        await RunPdvOperationAsync(() =>
        {
            EnsureCashSession();
            AddSelectedProductToCart();
            return Task.CompletedTask;
        });
    }

    private void OperatorLoginTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        LoadOperatorButton_Click(LoadOperatorButton, e);
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

    private void CartItemsGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete)
            return;
        e.Handled = true;
        RemoveItemButton_Click(RemoveItemButton, e);
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
        await RunPdvOperationAsync(async () =>
        {
            EnsureCashSession();
            if (_saleItems.Count == 0)
            {
                throw new InvalidOperationException("Inclua ao menos um item antes de registrar pagamento.");
            }

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

            _payments.Add(new UiPayment
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
                TefMetadataJson = tefAuthorization?.MetadataJson
            });

            PaymentReceivedTextBox.Text = FormatDecimal(CalculateRemainingAmount());
            UpdateTotals();
            SetOperationMessage(isTef
                ? $"Pagamento TEF {tefAuthorization?.Status ?? "capturado"} registrado pelo provider {_configuration.Tef.Provider}."
                : "Pagamento registrado.",
                isError: false);
        });
    }

    private async void RemovePaymentButton_Click(object sender, RoutedEventArgs e)
    {
        await RunPdvOperationAsync(async () =>
        {
            EnsureCashSession();
            if (PaymentsGrid.SelectedItem is not UiPayment selected)
            {
                throw new InvalidOperationException("Selecione um pagamento para remover.");
            }

            var authorization = RequireSupervisorAuthorization("remover pagamento");
            await RecordDraftAuditAsync(
                "payment_removed",
                authorization,
                BuildPaymentPayload(selected),
                CancellationToken.None);

            _payments.Remove(selected);
            UpdateTotals();
            SetOperationMessage("Pagamento removido com autorizacao de supervisor.", isError: false);
        });
    }

    private async void CompleteSaleButton_Click(object sender, RoutedEventArgs e)
    {
        await RunPdvOperationAsync(async () =>
        {
            EnsureCashSession();
            if (_saleItems.Count == 0)
            {
                throw new InvalidOperationException("Inclua ao menos um item antes de finalizar.");
            }

            var saleDiscount = ParseMoney(SaleDiscountTextBox.Text, "Desconto total");
            var auditEvents = BuildCompletedSaleAuditEvents(saleDiscount);
            var saleNumber = $"PDV-{DateTimeOffset.Now:yyyyMMddHHmmssfff}";
            var saleId = await _saleRepository.CreateCompletedSaleAsync(
                new CompletedSaleCommand(
                    CashSessionId: _currentCashSession!.CashSessionId,
                    OperatorId: _currentOperator!.OperatorId,
                    CustomerId: null,
                    SaleNumber: saleNumber,
                    Items: _saleItems
                        .Select(item => new CompletedSaleItemCommand(
                            item.ProductId,
                            item.LineNumber,
                            item.Quantity,
                            item.UnitPrice,
                            item.DiscountAmount))
                        .ToArray(),
                    Payments: _payments
                        .Select(payment => new CompletedSalePaymentCommand(
                            payment.Method,
                            payment.Amount,
                            payment.AuthorizationCode,
                            payment.PaymentSpeciesId,
                            payment.PaymentSpeciesExternalKey,
                            payment.PaymentSpeciesKind,
                            payment.PaymentConditionId,
                            payment.PaymentConditionExternalKey,
                            payment.Installments,
                            payment.RequiresTef,
                            payment.AllowsChange,
                            payment.TefMetadataJson))
                        .ToArray(),
                    DiscountAmount: saleDiscount,
                    AuditEvents: auditEvents),
                CancellationToken.None);

            var receipt = BuildReceiptData(saleId, saleNumber, saleDiscount);
            _lastReceipt = receipt;
            ReprintButton.Visibility = Visibility.Visible;
            ResetSale(clearMessage: false);
            SetOperationMessage($"Venda {saleNumber} finalizada e pendente de sincronizacao. ID: {saleId}.", isError: false);
            await RefreshCashSummaryAsync();
            await RefreshDatabaseStatusAsync();
            FocusProductEntry();
            new SaleReceiptWindow(receipt, _saleRepository) { Owner = this }.Show();
        });
    }

    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        new HelpWindow { Owner = this }.Show();
    }

    private void ReprintButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReceipt is not null)
        {
            new SaleReceiptWindow(_lastReceipt, _saleRepository) { Owner = this }.Show();
        }
    }

    private void CashSalesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentCashSession is null || _currentOperator is null)
            return;

        var sessionTitle = _currentOperator is not null
            ? $"{_currentOperator.DisplayName} — aberto em {_currentCashSession.OpenedAtUtc.LocalDateTime:dd/MM/yyyy HH:mm}"
            : $"Aberto em {_currentCashSession.OpenedAtUtc.LocalDateTime:dd/MM/yyyy HH:mm}";

        new CashSessionSalesWindow(
            _saleRepository,
            _operatorRepository,
            _currentOperator!,
            _currentCashSession.CashSessionId,
            sessionTitle)
        { Owner = this }.Show();
    }

    private async Task RefreshStatusAsync()
    {
        if (_isRefreshingStatus)
            return;
        _isRefreshingStatus = true;
        SetLoadingState(true);

        try
        {
            var status = await _httpClient.GetFromJsonAsync<SyncAgentStatusResponse>("/status");
            if (status is null)
            {
                ApplyOfflineState("Resposta vazia da API local do SyncAgent.");
                return;
            }

            ApplyStatus(status);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or NotSupportedException)
        {
            ApplyOfflineState("Nao foi possivel conectar ao SyncAgent local. Verifique se o servico Windows esta em execucao.");
        }
        finally
        {
            await RefreshDatabaseStatusAsync();
            await LoadPaymentCatalogAsync();
            _isRefreshingStatus = false;
            SetLoadingState(false);
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow { Owner = this };
        win.ShowDialog();
    }

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        CheckUpdateStatusText.Visibility = Visibility.Visible;
        CheckUpdateStatusText.BringIntoView();
        CheckUpdateStatusText.Text = "Solicitando verificacao de atualizacao...";
        CheckUpdateStatusText.SetResourceReference(ForegroundProperty, "StatusText");

        try
        {
            var response = await _httpClient.PostAsync("/check-update", null);
            if (response.IsSuccessStatusCode)
            {
                CheckUpdateStatusText.Text = "Solicitacao enviada. O agente verificara o ERP no proximo ciclo (ate 30s).";
                CheckUpdateStatusText.SetResourceReference(ForegroundProperty, "SuccessText");
            }
            else
            {
                CheckUpdateStatusText.Text = "Ja existe uma sincronizacao em andamento. Tente novamente em instantes.";
                CheckUpdateStatusText.SetResourceReference(ForegroundProperty, "WarningText");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            CheckUpdateStatusText.Text = "Nao foi possivel contatar o SyncAgent local. Verifique se o servico esta em execucao.";
            CheckUpdateStatusText.SetResourceReference(ForegroundProperty, "WarningText");
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private async Task<PdvOperator?> LoadOperatorAsync()
    {
        var login = OperatorLoginTextBox.Text.Trim();
        var operatorRecord = await _operatorRepository.FindActiveByLoginAsync(login, CancellationToken.None);
        if (operatorRecord is null)
        {
            _currentOperator = null;
            _currentCashSession = null;
            ApplyCurrentSession();
            await RefreshCashSummaryAsync();
            SetOperationMessage($"Operador ativo nao encontrado: {login}.", isError: true);
            return null;
        }

        _currentOperator = operatorRecord;
        _currentCashSession = await _cashSessionRepository.FindOpenByOperatorAsync(
            operatorRecord.OperatorId,
            CancellationToken.None);
        ApplyCurrentSession();
        await RefreshCashSummaryAsync();
        SetOperationMessage($"Operador carregado: {operatorRecord.DisplayName}.", isError: false);
        return operatorRecord;
    }

    private async Task SearchProductsAsync()
    {
        var query = ProductEntryTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("Informe um codigo, codigo de barras ou nome de produto.");
        }

        var products = await _productRepository.SearchForSaleAsync(query, limit: 20, CancellationToken.None);
        _productSearchResults.Clear();
        foreach (var product in products)
        {
            _productSearchResults.Add(new UiProductSearchResult(product));
        }

        if (products.Count == 0)
        {
            _selectedProduct = null;
            UnitPriceTextBox.Text = "0,00";
            UpdateSelectedProductTotal();
            throw new InvalidOperationException("Produto nao encontrado no catalogo local.");
        }

        if (products.Count == 1)
        {
            SelectProduct(products[0]);
        }

        SetOperationMessage(products.Count == 1
            ? "Produto encontrado. Pressione Enter ou clique Adicionar."
            : $"Foram encontrados {products.Count} produtos. Use setas + Enter ou duplo clique para selecionar.",
            isError: false);
    }

    private void SelectProduct(PdvProduct product)
    {
        _selectedProduct = product;
        ProductEntryTextBox.Text = DisplayCode(product);
        UnitPriceTextBox.Text = FormatDecimal(product.Price);
        ItemDiscountTextBox.Text = "0,00";
        UpdateSelectedProductTotal();
        SetOperationMessage($"Produto selecionado: {product.Name}.", isError: false);
        QuantityTextBox.Focus();
        QuantityTextBox.SelectAll();
    }

    private void AddSelectedProductToCart()
    {
        if (_selectedProduct is null)
        {
            throw new InvalidOperationException("Busque e selecione um produto antes de adicionar.");
        }

        var quantity = ParseMoney(QuantityTextBox.Text, "Quantidade");
        var discount = ParseMoney(ItemDiscountTextBox.Text, "Desconto do item");
        if (quantity <= 0)
        {
            throw new ArgumentException("Quantidade deve ser maior que zero.");
        }

        if (discount > quantity * _selectedProduct.Price)
        {
            throw new ArgumentException("Desconto do item nao pode ser maior que o total bruto do item.");
        }

        SupervisorAuthorization? discountAuthorization = null;
        if (discount > 0)
        {
            discountAuthorization = RequireSupervisorAuthorization("aplicar desconto no item");
        }

        _saleItems.Add(new UiSaleItem
        {
            ProductId = _selectedProduct.ProductId,
            ExternalKey = _selectedProduct.ExternalKey,
            Sku = _selectedProduct.Sku,
            Barcode = _selectedProduct.Barcode,
            Name = _selectedProduct.Name,
            LineNumber = _saleItems.Count + 1,
            Quantity = quantity,
            UnitPrice = _selectedProduct.Price,
            DiscountAmount = discount,
            DiscountSupervisorOperatorId = discountAuthorization?.Supervisor.OperatorId,
            DiscountSupervisorLogin = discountAuthorization?.Supervisor.Login,
            DiscountReason = discountAuthorization?.Reason
        });

        CartItemsGrid.UpdateLayout();
        CartItemsGrid.ScrollIntoView(_saleItems[^1]);

        ProductEntryTextBox.Clear();
        QuantityTextBox.Text = "1";
        UnitPriceTextBox.Text = "0,00";
        ItemDiscountTextBox.Text = "0,00";
        _selectedProduct = null;
        _productSearchResults.Clear();
        UpdateTotals();
        SaveDraftToBackground();
        SetOperationMessage("Item adicionado ao carrinho.", isError: false);
        FocusProductEntry();
    }

    private SaleReceiptData BuildReceiptData(Guid saleId, string saleNumber, decimal saleDiscount)
    {
        var subtotal = _saleItems.Sum(item => item.Quantity * item.UnitPrice);
        var itemDiscount = _saleItems.Sum(item => item.DiscountAmount);
        var total = Math.Max(0, subtotal - itemDiscount - saleDiscount);
        var change = _payments.Sum(p => p.ChangeAmount);
        return new SaleReceiptData(
            saleId,
            saleNumber,
            DateTimeOffset.Now,
            _currentOperator!.DisplayName,
            _saleItems.ToList(),
            subtotal,
            itemDiscount,
            saleDiscount,
            total,
            _payments.ToList(),
            change);
    }

    private async Task LoadDraftIfAvailableAsync()
    {
        if (_currentCashSession is null)
            return;

        try
        {
            var draft = await _saleRepository.GetDraftAsync(_currentCashSession.CashSessionId, CancellationToken.None);
            if (draft is null || draft.Items.Count == 0)
                return;

            foreach (var item in draft.Items)
            {
                _saleItems.Add(new UiSaleItem
                {
                    ProductId = item.ProductId,
                    ExternalKey = item.ExternalKey,
                    Sku = item.Sku,
                    Barcode = item.Barcode,
                    Name = item.Name,
                    LineNumber = item.LineNumber,
                    Quantity = item.Quantity,
                    UnitPrice = item.UnitPrice,
                    DiscountAmount = item.DiscountAmount
                });
            }

            if (draft.SaleDiscount > 0)
                SaleDiscountTextBox.Text = FormatDecimal(draft.SaleDiscount);

            UpdateTotals();
            SetOperationMessage(
                $"Rascunho restaurado: {draft.Items.Count} {(draft.Items.Count == 1 ? "item" : "itens")} da venda anterior.",
                isError: false);
        }
        catch
        {
            // draft é best-effort — falha silenciosa
        }
    }

    private void SaveDraftToBackground()
    {
        if (_currentCashSession is null || _currentOperator is null || _saleItems.Count == 0)
            return;

        var items = _saleItems
            .Select(i => new PdvDraftItemCommand(i.ProductId, i.LineNumber, i.Quantity, i.UnitPrice, i.DiscountAmount))
            .ToList();
        var cashSessionId = _currentCashSession.CashSessionId;
        var operatorId = _currentOperator.OperatorId;
        var saleDiscount = TryParseMoney(SaleDiscountTextBox.Text, out var d) ? d : 0m;

        _ = _saleRepository
            .SaveDraftAsync(cashSessionId, operatorId, items, saleDiscount, CancellationToken.None)
            .ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);
    }

    private void DeleteDraftInBackground()
    {
        if (_currentCashSession is null)
            return;

        var cashSessionId = _currentCashSession.CashSessionId;
        _ = _saleRepository
            .DeleteDraftAsync(cashSessionId, CancellationToken.None)
            .ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);
    }

    private void RunPdvOperation(Action operation)
    {
        try
        {
            operation();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Npgsql.NpgsqlException or TimeoutException)
        {
            SetOperationMessage(ex.Message, isError: true);
        }
    }

    private async Task RunPdvOperationAsync(Func<Task> operation)
    {
        SetLoadingState(true);
        try
        {
            await operation();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Npgsql.NpgsqlException or TimeoutException)
        {
            SetOperationMessage(ex.Message, isError: true);
        }
        finally
        {
            SetLoadingState(false);
        }
    }

    private void EnsureCashSession()
    {
        if (_currentOperator is null)
        {
            throw new InvalidOperationException("Carregue um operador antes de operar o caixa.");
        }

        if (_currentCashSession is null)
        {
            throw new InvalidOperationException("Abra ou carregue um caixa antes de vender.");
        }
    }

    private SupervisorAuthorization RequireSupervisorAuthorization(string operationDescription)
    {
        var authorization = SupervisorAuthorizationDialog.Request(this, _operatorRepository, operationDescription);
        if (authorization is null)
        {
            throw new InvalidOperationException($"Autorizacao de supervisor cancelada para {operationDescription}.");
        }

        return authorization;
    }

    private async Task RecordDraftAuditAsync(
        string operationType,
        SupervisorAuthorization authorization,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        EnsureCashSession();
        await _operationAuditRepository.InsertAsync(
            new PdvOperationAuditCommand(
                operationType,
                _currentCashSession!.CashSessionId,
                SaleId: null,
                _currentOperator!.OperatorId,
                authorization.Supervisor.OperatorId,
                authorization.Reason,
                payloadJson),
            cancellationToken);
    }

    private IReadOnlyList<CompletedSaleAuditCommand> BuildCompletedSaleAuditEvents(decimal saleDiscount)
    {
        var auditEvents = new List<CompletedSaleAuditCommand>();
        foreach (var item in _saleItems.Where(item => item.DiscountAmount > 0))
        {
            if (item.DiscountSupervisorOperatorId is null)
            {
                throw new InvalidOperationException($"Item {item.LineNumber} possui desconto sem supervisor autorizado.");
            }

            auditEvents.Add(new CompletedSaleAuditCommand(
                "item_discount_applied",
                item.DiscountSupervisorOperatorId.Value,
                item.DiscountReason,
                BuildSaleItemPayload(item)));
        }

        if (saleDiscount > 0)
        {
            var authorization = RequireSupervisorAuthorization("aplicar desconto total");
            auditEvents.Add(new CompletedSaleAuditCommand(
                "sale_discount_applied",
                authorization.Supervisor.OperatorId,
                authorization.Reason,
                JsonSerializer.Serialize(new
                {
                    discount_amount = saleDiscount,
                    subtotal_amount = _saleItems.Sum(item => item.Quantity * item.UnitPrice),
                    item_discount_amount = _saleItems.Sum(item => item.DiscountAmount),
                    total_amount = _saleItems.Sum(item => item.Quantity * item.UnitPrice) - _saleItems.Sum(item => item.DiscountAmount) - saleDiscount
                }, AuditJsonOptions)));
        }

        return auditEvents;
    }

    private string BuildCurrentDraftPayload()
    {
        return JsonSerializer.Serialize(new
        {
            items = _saleItems.Select(item => new
            {
                item.LineNumber,
                item.ProductId,
                item.ExternalKey,
                item.Sku,
                item.Barcode,
                item.Name,
                item.Quantity,
                item.UnitPrice,
                item.DiscountAmount,
                item.TotalAmount
            }),
            payments = _payments.Select(payment => new
            {
                payment.Method,
                payment.Condition,
                payment.Amount,
                payment.ReceivedAmount,
                payment.ChangeAmount,
                payment.AuthorizationCode,
                payment.TefMetadataJson
            }),
            sale_discount_amount = TryParseMoney(SaleDiscountTextBox.Text, out var saleDiscount) ? saleDiscount : 0,
            total_amount = TryParseMoney(TotalValue.Text.Replace("R$", "", StringComparison.Ordinal), out var total) ? total : 0
        }, AuditJsonOptions);
    }

    private static string BuildSaleItemPayload(UiSaleItem item)
    {
        return JsonSerializer.Serialize(new
        {
            item.LineNumber,
            item.ProductId,
            item.ExternalKey,
            item.Sku,
            item.Barcode,
            item.Name,
            item.Quantity,
            item.UnitPrice,
            item.DiscountAmount,
            item.TotalAmount,
            item.DiscountSupervisorLogin
        }, AuditJsonOptions);
    }

    private static string BuildPaymentPayload(UiPayment payment)
    {
        return JsonSerializer.Serialize(new
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
        }, AuditJsonOptions);
    }

    private void ApplyCurrentSession()
    {
        CurrentOperatorValue.Text = _currentOperator is null
            ? "-"
            : $"{_currentOperator.DisplayName} ({_currentOperator.Login} / { _currentOperator.Role})";
        CurrentCashSessionValue.Text = _currentCashSession is null
            ? "-"
            : $"{_currentCashSession.CashSessionId} / {_currentCashSession.Status}";
        if (_currentCashSession is null && CashSummaryValue is not null)
        {
            CashSummaryValue.Text = "-";
        }

        ApplySaleGate();
    }

    private async Task RefreshCashSummaryAsync()
    {
        if (CashSummaryValue is null)
        {
            return;
        }

        if (_currentCashSession is null)
        {
            CashSummaryValue.Text = "-";
            return;
        }

        var summary = await _cashMovementRepository.GetSummaryAsync(
            _currentCashSession.CashSessionId,
            CancellationToken.None);
        CashSummaryValue.Text = FormatCashSummary(summary);
        ClosingAmountTextBox.Text = FormatDecimal(summary.ExpectedCashAmount);
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

    private void ApplySaleGate()
    {
        var canSell = _currentOperator is not null && _currentCashSession is not null;
        SalePanel.IsEnabled = canSell;
        AddPaymentButton.IsEnabled = canSell;
        RemovePaymentButton.IsEnabled = canSell;
        CompleteSaleButton.IsEnabled = canSell;
        PaymentMethodComboBox.IsEnabled = canSell;
        PaymentReceivedTextBox.IsEnabled = canSell;
        CloseCashButton.IsEnabled = _currentCashSession is not null;
        CashSalesButton.IsEnabled = _currentCashSession is not null;
        CashMovementAmountTextBox.IsEnabled = canSell;
        CashSupplyButton.IsEnabled = canSell;
        CashWithdrawalButton.IsEnabled = canSell;
        PaymentConditionComboBox.IsEnabled = canSell;
        SaleGateValue.Text = canSell
            ? "Caixa aberto. Lance produtos pelo campo principal."
            : "Venda bloqueada. Carregue operador e abra caixa.";
        SaleGateValue.Foreground = BrushFromHex(canSell ? "#166534" : "#64748B");
    }

    private void ResetSale(bool clearMessage)
    {
        DeleteDraftInBackground();
        _selectedProduct = null;
        _productSearchResults.Clear();
        _saleItems.Clear();
        _payments.Clear();
        ProductEntryTextBox.Clear();
        QuantityTextBox.Text = "1";
        UnitPriceTextBox.Text = "0,00";
        ItemDiscountTextBox.Text = "0,00";
        SaleDiscountTextBox.Text = "0,00";
        SaleDiscountTextBox.Background = Brushes.White;
        PaymentMethodComboBox.Text = "dinheiro";
        PaymentConditionComboBox.Text = "A vista";
        PaymentReceivedTextBox.Text = "0,00";
        SelectDefaultPaymentCatalogItems();
        UpdateSelectedProductTotal();
        UpdateTotals();
        if (clearMessage)
        {
            SetOperationMessage("Venda limpa.", isError: false);
        }
    }

    private void RenumberSaleItems()
    {
        for (var index = 0; index < _saleItems.Count; index++)
        {
            _saleItems[index].LineNumber = index + 1;
        }

        CartItemsGrid.Items.Refresh();
    }

    private void UpdateSelectedProductTotal()
    {
        if (SelectedProductTotalValue is null)
        {
            return;
        }

        if (_selectedProduct is null || !TryParseMoney(QuantityTextBox.Text, out var quantity) || !TryParseMoney(ItemDiscountTextBox.Text, out var discount))
        {
            SelectedProductTotalValue.Text = FormatMoney(0);
            return;
        }

        SelectedProductTotalValue.Text = FormatMoney(Math.Max(0, quantity * _selectedProduct.Price - discount));
    }

    private void UpdateTotals()
    {
        if (SubtotalValue is null || TotalValue is null || PaidValue is null || RemainingValue is null || ChangeValue is null)
        {
            return;
        }

        var subtotal = _saleItems.Sum(item => item.Quantity * item.UnitPrice);
        var itemDiscount = _saleItems.Sum(item => item.DiscountAmount);
        var saleDiscount = TryParseMoney(SaleDiscountTextBox.Text, out var parsedSaleDiscount)
            ? parsedSaleDiscount
            : 0;
        var total = Math.Max(0, subtotal - itemDiscount - saleDiscount);
        var paid = _payments.Sum(payment => payment.Amount);
        var remaining = Math.Max(0, total - paid);
        var change = _payments.Sum(payment => payment.ChangeAmount);

        SubtotalValue.Text = FormatMoney(subtotal);
        TotalValue.Text = FormatMoney(total);
        PaidValue.Text = FormatMoney(paid);
        RemainingValue.Text = FormatMoney(remaining);
        RemainingValue.Foreground = remaining > 0 ? BrushFromHex("#DC2626") : BrushFromHex("#0F172A");
        ChangeValue.Text = FormatMoney(change);

        if (SaleTitleValue is not null)
        {
            SaleTitleValue.Text = _saleItems.Count == 0
                ? "Venda"
                : $"Venda — {_saleItems.Count} {(_saleItems.Count == 1 ? "item" : "itens")} — {FormatMoney(total)}";
        }
    }

    private decimal CalculateRemainingAmount()
    {
        var subtotal = _saleItems.Sum(item => item.Quantity * item.UnitPrice);
        var itemDiscount = _saleItems.Sum(item => item.DiscountAmount);
        var saleDiscount = ParseMoney(SaleDiscountTextBox.Text, "Desconto total");
        var total = subtotal - itemDiscount - saleDiscount;
        var paid = _payments.Sum(payment => payment.Amount);
        if (total < 0)
        {
            throw new ArgumentException("Desconto total nao pode deixar a venda negativa.");
        }

        return Math.Max(0, total - paid);
    }

    private void SetOperationMessage(string message, bool isError)
    {
        OperationMessageValue.Text = message;
        OperationMessageValue.Foreground = BrushFromHex(isError ? "#991B1B" : "#166534");
    }

    private async Task RefreshDatabaseStatusAsync()
    {
        var status = await _databaseStatusReader.ReadAsync(CancellationToken.None);
        ApplyDatabaseStatus(status);
    }

    private async Task LoadPaymentCatalogAsync()
    {
        try
        {
            var species = await _paymentCatalogRepository.GetActiveSpeciesAsync(CancellationToken.None);
            var conditions = await _paymentCatalogRepository.GetActiveConditionsAsync(CancellationToken.None);

            _paymentSpecies.Clear();
            foreach (var item in species.Select(UiPaymentSpecies.FromModel))
            {
                _paymentSpecies.Add(item);
            }

            _paymentConditions.Clear();
            foreach (var item in conditions.Select(UiPaymentCondition.FromModel))
            {
                _paymentConditions.Add(item);
            }

            if (_paymentSpecies.Count == 0 || _paymentConditions.Count == 0)
            {
                ApplyFallbackPaymentCatalog();
            }
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException or TimeoutException)
        {
            ApplyFallbackPaymentCatalog();
        }

        SelectDefaultPaymentCatalogItems();
    }

    private void ApplyFallbackPaymentCatalog()
    {
        _paymentSpecies.Clear();
        _paymentSpecies.Add(new UiPaymentSpecies(null, "fallback-dinheiro", "dinheiro", "cash", false, true));
        _paymentSpecies.Add(new UiPaymentSpecies(null, "fallback-pix", "pix", "pix", false, false));
        _paymentSpecies.Add(new UiPaymentSpecies(null, "fallback-cartao-debito", "cartao_debito_tef_simulado", "card", true, false));
        _paymentSpecies.Add(new UiPaymentSpecies(null, "fallback-cartao-credito", "cartao_credito_tef_simulado", "card", true, false));

        _paymentConditions.Clear();
        _paymentConditions.Add(new UiPaymentCondition(null, "fallback-a-vista", "A vista", 1, 0, 0));
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

    private void ApplyStatus(SyncAgentStatusResponse status)
    {
        var provisionedText = status.Provisioned ? "Ativado" : "Nao ativado";
        var heartbeatText = status.LastHeartbeatSucceeded == true
            ? $"Online / {status.LastHeartbeatConnectivity ?? "-"}"
            : "Sem heartbeat";

        ProvisioningValue.Text = provisionedText;
        RuntimeValue.Text = EmptyAsDash(status.RuntimeStatus);
        HeartbeatValue.Text = heartbeatText;
        PendingValue.Text = status.PendingOutboxEvents.ToString();
        DeadLetterValue.Text = status.DeadLetterEvents.ToString();
        InstanceValue.Text = EmptyAsDash(status.InstanceId);
        TenantValue.Text = EmptyAsDash(status.ErpTenantId);
        ErpApiValue.Text = EmptyAsDash(status.ErpApiBaseUrl);
        DatabaseValue.Text = EmptyAsDash(status.Database);
        LastErrorValue.Text = EmptyAsDash(status.LastError);

        if (!status.Provisioned)
        {
            SetBanner(
                "Esta instalacao ainda nao esta ativada. Clique em Ativacao para conectar ao ERP.",
                "#FEF3C7",
                "#F59E0B",
                "#92400E");
        }
        else if (status.LastHeartbeatSucceeded != true)
        {
            SetBanner(
                "Instalacao ativada, mas o ultimo heartbeat nao confirmou conectividade com o ERP.",
                "#FEF3C7",
                "#F59E0B",
                "#92400E");
        }
        else if (status.PendingOutboxEvents > 0 || status.DeadLetterEvents > 0)
        {
            SetBanner(
                "SyncAgent conectado. Existem eventos pendentes ou dead-letter para acompanhamento.",
                "#EFF6FF",
                "#BFDBFE",
                "#1E40AF");
        }
        else
        {
            SetBanner(
                "SyncAgent conectado, ativado e sem pendencias locais.",
                "#DCFCE7",
                "#86EFAC",
                "#166534");
        }

        FooterText.Text = $"Atualizado em {DateTime.Now:dd/MM/yyyy HH:mm:ss}. API local: {_configuration.SyncAgentLocalApiBaseUrl}";
    }

    private void ApplyDatabaseStatus(PdvDatabaseStatus status)
    {
        if (!status.IsAvailable)
        {
            PgVectorValue.Text = "-";
            PdvSchemaValue.Text = "indisponivel";
            OperatorsValue.Text = "-";
            ProductsValue.Text = "-";
            SalesValue.Text = "-";
            OpenCashSessionsValue.Text = "-";
            DatabaseValue.Text = "-";
            DatabaseErrorValue.Text = EmptyAsDash(status.Error);
            return;
        }

        PgVectorValue.Text = EmptyAsDash(status.PgVectorVersion);
        PdvSchemaValue.Text = status.PdvSchemaExists
            ? $"ok / {status.PdvTableCount} tabelas"
            : "nao criado";
        OperatorsValue.Text = status.OperatorCount.ToString();
        ProductsValue.Text = status.ProductCount.ToString();
        SalesValue.Text = status.SaleCount.ToString();
        OpenCashSessionsValue.Text = status.OpenCashSessionCount.ToString();
        OperatorsImportPanel.Visibility = status.PdvSchemaExists && status.OperatorCount == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        DatabaseValue.Text = $"{EmptyAsDash(status.DatabaseName)} / {EmptyAsDash(status.UserName)} / {EmptyAsDash(status.TimeZone)}";
        DatabaseErrorValue.Text = "-";
    }

    private void ApplyOfflineState(string message)
    {
        ProvisioningValue.Text = "-";
        RuntimeValue.Text = "offline";
        HeartbeatValue.Text = "-";
        PendingValue.Text = "-";
        DeadLetterValue.Text = "-";
        PgVectorValue.Text = "-";
        PdvSchemaValue.Text = "-";
        OperatorsValue.Text = "-";
        ProductsValue.Text = "-";
        SalesValue.Text = "-";
        OpenCashSessionsValue.Text = "-";
        OperatorsImportPanel.Visibility = Visibility.Collapsed;
        InstanceValue.Text = "-";
        TenantValue.Text = "-";
        ErpApiValue.Text = "-";
        DatabaseValue.Text = "-";
        LastErrorValue.Text = message;

        SetBanner(message, "#FEE2E2", "#FCA5A5", "#991B1B");
        FooterText.Text = $"Falha na leitura em {DateTime.Now:dd/MM/yyyy HH:mm:ss}. API local: {_configuration.SyncAgentLocalApiBaseUrl}";
    }

    private void SetLoadingState(bool isLoading)
    {
        RefreshButton.IsEnabled = !isLoading;
        RefreshButton.Content = isLoading ? "Atualizando..." : "Atualizar";
    }

    private void SetBanner(string message, string background, string border, string foreground)
    {
        BannerText.Text = message;
        BannerBorder.Background = BrushFromHex(background);
        BannerBorder.BorderBrush = BrushFromHex(border);
        BannerText.Foreground = BrushFromHex(foreground);
    }

    private void FocusProductEntry()
    {
        if (ProductEntryTextBox.IsEnabled)
        {
            ProductEntryTextBox.Focus();
        }
    }

    private static string ReadPaymentMethod(System.Windows.Controls.ComboBox comboBox)
    {
        return comboBox.Text.Trim();
    }

    private UiPaymentSpecies ReadPaymentMethod()
    {
        if (PaymentMethodComboBox.SelectedItem is UiPaymentSpecies selected)
        {
            return selected;
        }

        var methodName = ReadPaymentMethod(PaymentMethodComboBox);
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

        var conditionName = ReadPaymentMethod(PaymentConditionComboBox);
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

}

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
    private readonly DispatcherTimer _clockTimer;
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
    private bool _startupCashPromptShown;
    private decimal _saleDiscount;

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
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => ClockText.Text = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss");

        InitializeComponent();
        ProductSearchResultsGrid.ItemsSource = _productSearchResults;
        CartItemsGrid.ItemsSource = _saleItems;
        LocalApiValue.Text = _configuration.SyncAgentLocalApiBaseUrl.ToString();
        ResetSale(clearMessage: false);
        ApplyCurrentSession();
    }

    protected override void OnClosed(EventArgs e)
    {
        _statusRefreshTimer.Stop();
        _clockTimer.Stop();
        _httpClient.Dispose();
        base.OnClosed(e);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        VersionText.Text = "Versao " + (typeof(MainWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "desconhecida");
        ClockText.Text = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss");
        _clockTimer.Start();
        await _operationAuditRepository.EnsureSchemaAsync(CancellationToken.None);
        await _cashMovementRepository.EnsureSchemaAsync(CancellationToken.None);
        await RefreshStatusAsync();
        _statusRefreshTimer.Start();

        if (_initialOperator is not null)
        {
            _currentOperator = _initialOperator;
            _currentCashSession = await _cashSessionRepository.FindOpenByOperatorAsync(
                _initialOperator.OperatorId, CancellationToken.None);
            ApplyCurrentSession();
            SetOperationMessage($"Operador: {_initialOperator.DisplayName}.", isError: false);

            if (_currentCashSession is null)
            {
                _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(TryOpenStartupCashWindow));
            }
            else
            {
                await LoadDraftIfAvailableAsync();
                FocusProductEntry();
            }
        }
    }

    private void TryOpenStartupCashWindow()
    {
        if (_startupCashPromptShown)
        {
            return;
        }

        _startupCashPromptShown = true;
        if (_currentCashSession is null && _currentOperator is not null)
        {
            OpenCashWindow();
        }
    }

    private void CashButton_Click(object sender, RoutedEventArgs e)
    {
        OpenCashWindow();
    }

    private void SwitchOperatorButton_Click(object sender, RoutedEventArgs e)
    {
        SwitchOperator();
    }

    private void OpenCashWindow()
    {
        if (_currentOperator is null)
        {
            return;
        }

        var hadSession = _currentCashSession is not null;
        var window = new CashSessionWindow(
            _currentOperator,
            _currentCashSession,
            _cashSessionRepository,
            _cashMovementRepository,
            _operatorRepository,
            hasPendingSale: _saleItems.Count > 0 || _payments.Count > 0)
        { Owner = this };
        window.ShowDialog();

        _currentCashSession = window.CurrentSession;
        ApplyCurrentSession();

        if (!hadSession && _currentCashSession is not null)
        {
            _ = HandleCashOpenedAsync();
        }
        else if (hadSession && _currentCashSession is null)
        {
            SetOperationMessage("Caixa fechado.", isError: false);
            _ = RefreshDatabaseStatusAsync();
        }
    }

    private async Task HandleCashOpenedAsync()
    {
        await RunPdvOperationAsync(async () =>
        {
            SetOperationMessage("Caixa aberto. Lance produtos pelo campo principal.", isError: false);
            await LoadDraftIfAvailableAsync();
            FocusProductEntry();
            await RefreshDatabaseStatusAsync();
        });
    }

    private void SwitchOperator()
    {
        if (_saleItems.Count > 0 || _payments.Count > 0)
        {
            SetOperationMessage("Finalize ou cancele a venda em andamento antes de trocar de operador.", isError: true);
            return;
        }

        var login = new LoginWindow(_configuration.PdvLocalConnectionString) { Owner = this };
        if (login.ShowDialog() != true || login.AuthenticatedOperator is null)
        {
            return;
        }

        _ = ApplyOperatorAsync(login.AuthenticatedOperator);
    }

    private async Task ApplyOperatorAsync(PdvOperator newOperator)
    {
        await RunPdvOperationAsync(async () =>
        {
            var previousOperator = _currentOperator;
            var previousSession = _currentCashSession;

            _currentOperator = newOperator;
            _currentCashSession = await _cashSessionRepository.FindOpenByOperatorAsync(
                newOperator.OperatorId,
                CancellationToken.None);
            ApplyCurrentSession();

            var previousCashStaysOpen = previousSession is not null
                && previousOperator is not null
                && previousOperator.OperatorId != newOperator.OperatorId;
            SetOperationMessage(previousCashStaysOpen
                ? $"Operador: {newOperator.DisplayName}. O caixa de {previousOperator!.DisplayName} permanece aberto."
                : $"Operador: {newOperator.DisplayName}.",
                isError: false);

            if (_currentCashSession is null)
            {
                OpenCashWindow();
            }
            else
            {
                await LoadDraftIfAvailableAsync();
                FocusProductEntry();
            }
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

    private void MainWindow_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.System when e.SystemKey == Key.F10:
            case Key.F10:
            case Key.F12:
                e.Handled = true;
                StartPayment();
                break;
            case Key.F2:
                e.Handled = true;
                NewSaleButton_Click(NewSaleButton, e);
                break;
            case Key.F1:
                e.Handled = true;
                HelpButton_Click(HelpButton, e);
                break;
            case Key.F4:
                e.Handled = true;
                OpenCashWindow();
                break;
            case Key.F11:
                e.Handled = true;
                SwitchOperator();
                break;
            case Key.D when Keyboard.Modifiers == ModifierKeys.Control:
                e.Handled = true;
                OpenTechnicalDetailsWindow();
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

    private void CartItemsGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete)
            return;
        e.Handled = true;
        RemoveItemButton_Click(RemoveItemButton, e);
    }

    private void PaymentButton_Click(object sender, RoutedEventArgs e)
    {
        StartPayment();
    }

    private void StartPayment()
    {
        RunPdvOperation(() =>
        {
            EnsureCashSession();
            if (_saleItems.Count == 0)
            {
                throw new InvalidOperationException("Inclua ao menos um item antes de finalizar.");
            }

            var subtotal = _saleItems.Sum(item => item.Quantity * item.UnitPrice);
            var itemDiscount = _saleItems.Sum(item => item.DiscountAmount);
            var window = new PaymentWindow(
                subtotal,
                itemDiscount,
                _saleDiscount,
                _payments,
                _paymentSpecies,
                _paymentConditions,
                _tefPaymentProvider,
                _configuration.Tef.Provider,
                _operatorRepository,
                (operationType, authorization, payload) =>
                    RecordDraftAuditAsync(operationType, authorization, payload, CancellationToken.None))
            { Owner = this };

            var confirmed = window.ShowDialog() == true;
            _saleDiscount = window.SaleDiscount;
            UpdateTotals();
            SaveDraftToBackground();

            if (confirmed)
            {
                _ = CompleteSaleAsync();
            }
        });
    }

    private async Task CompleteSaleAsync()
    {
        await RunPdvOperationAsync(async () =>
        {
            EnsureCashSession();
            if (_saleItems.Count == 0)
            {
                throw new InvalidOperationException("Inclua ao menos um item antes de finalizar.");
            }

            var saleDiscount = _saleDiscount;
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
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow { Owner = this };
        win.ShowDialog();
    }

    private void OpenTechnicalDetailsWindow()
    {
        new TechnicalDetailsWindow { Owner = this }.ShowDialog();
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
                _saleDiscount = draft.SaleDiscount;

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
        var saleDiscount = _saleDiscount;

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
        try
        {
            await operation();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Npgsql.NpgsqlException or TimeoutException)
        {
            SetOperationMessage(ex.Message, isError: true);
        }
    }

    private void EnsureCashSession()
    {
        if (_currentOperator is null)
        {
            throw new InvalidOperationException("Faca login para operar o PDV.");
        }

        if (_currentCashSession is null)
        {
            throw new InvalidOperationException("Abra o caixa (F4) antes de vender.");
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
            sale_discount_amount = _saleDiscount,
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

    private void ApplyCurrentSession()
    {
        OperatorStatusText.Text = _currentOperator is null
            ? "Operador: -"
            : $"Operador: {_currentOperator.DisplayName} ({_currentOperator.Login})";
        CashStatusText.Text = _currentCashSession is null
            ? "Caixa: fechado — F4 para abrir"
            : $"Caixa: aberto desde {_currentCashSession.OpenedAtUtc.LocalDateTime:dd/MM/yyyy HH:mm}";

        ApplySaleGate();
    }

    private void ApplySaleGate()
    {
        var canSell = _currentOperator is not null && _currentCashSession is not null;
        SalePanel.IsEnabled = canSell;
        PaymentButton.IsEnabled = canSell;
        CashSalesButton.IsEnabled = _currentCashSession is not null;
        SaleGateValue.Text = canSell
            ? "Caixa aberto. Lance produtos pelo campo principal."
            : "Venda bloqueada. Caixa fechado — pressione F4 para abrir.";
        SaleGateValue.Foreground = BrushFromHex(canSell ? "#166534" : "#64748B");
    }

    private void ResetSale(bool clearMessage)
    {
        DeleteDraftInBackground();
        _selectedProduct = null;
        _productSearchResults.Clear();
        _saleItems.Clear();
        _payments.Clear();
        _saleDiscount = 0;
        ProductEntryTextBox.Clear();
        QuantityTextBox.Text = "1";
        UnitPriceTextBox.Text = "0,00";
        ItemDiscountTextBox.Text = "0,00";
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
        if (SubtotalValue is null || TotalValue is null)
        {
            return;
        }

        var subtotal = _saleItems.Sum(item => item.Quantity * item.UnitPrice);
        var itemDiscount = _saleItems.Sum(item => item.DiscountAmount);
        var total = Math.Max(0, subtotal - itemDiscount - _saleDiscount);

        SubtotalValue.Text = FormatMoney(subtotal);
        TotalValue.Text = FormatMoney(total);

        if (SaleTitleValue is not null)
        {
            SaleTitleValue.Text = _saleItems.Count == 0
                ? "Venda"
                : $"Venda — {_saleItems.Count} {(_saleItems.Count == 1 ? "item" : "itens")} — {FormatMoney(total)}";
        }
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

    private void ApplyStatus(SyncAgentStatusResponse status)
    {
        if (!status.Provisioned)
        {
            SetBanner(
                "Esta instalacao ainda nao esta ativada. Abra Configuracoes > Diagnostico e ativacao para conectar ao ERP.",
                "#FEF3C7",
                "#F59E0B",
                "#92400E");
            SyncStatusText.Text = "Sync: nao ativado";
        }
        else if (status.LastHeartbeatSucceeded != true)
        {
            SetBanner(
                "Instalacao ativada, mas o ultimo heartbeat nao confirmou conectividade com o ERP.",
                "#FEF3C7",
                "#F59E0B",
                "#92400E");
            SyncStatusText.Text = "Sync: sem heartbeat";
        }
        else if (status.PendingOutboxEvents > 0 || status.DeadLetterEvents > 0)
        {
            BannerBorder.Visibility = Visibility.Collapsed;
            SyncStatusText.Text = status.DeadLetterEvents > 0
                ? $"Sync: {status.PendingOutboxEvents} pendente(s), {status.DeadLetterEvents} dead-letter"
                : $"Sync: {status.PendingOutboxEvents} pendente(s)";
        }
        else
        {
            BannerBorder.Visibility = Visibility.Collapsed;
            SyncStatusText.Text = "Sync: OK";
        }
    }

    private void ApplyDatabaseStatus(PdvDatabaseStatus status)
    {
        OperatorsImportPanel.Visibility = status.IsAvailable && status.PdvSchemaExists && status.OperatorCount == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ApplyOfflineState(string message)
    {
        OperatorsImportPanel.Visibility = Visibility.Collapsed;
        SetBanner(message, "#FEE2E2", "#FCA5A5", "#991B1B");
        SyncStatusText.Text = "Sync: offline";
    }

    private void SetBanner(string message, string background, string border, string foreground)
    {
        BannerBorder.Visibility = Visibility.Visible;
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

}

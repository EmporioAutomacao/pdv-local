using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using PdvLocal.Core;
using static PdvLocal.App.PdvUiFormatting;

namespace PdvLocal.App;

public partial class PriceCheckWindow : Window
{
    private readonly PdvProductRepository _productRepository;
    private readonly ObservableCollection<UiProductSearchResult> _results = [];

    public PriceCheckWindow(PdvProductRepository productRepository)
    {
        _productRepository = productRepository;
        InitializeComponent();
        PriceResultsGrid.ItemsSource = _results;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        PriceQueryTextBox.Focus();
    }

    private async void PriceSearchButton_Click(object sender, RoutedEventArgs e)
    {
        await SearchAsync();
    }

    private async void PriceQueryTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        await SearchAsync();
    }

    private async void PriceResultsGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (PriceResultsGrid.SelectedItem is not UiProductSearchResult selected)
        {
            return;
        }

        PriceProductNameText.Text = selected.Name;
        PriceValueText.Text = FormatMoney(selected.Product.Price);
        UnitPricesText.Text = string.Empty;

        try
        {
            var units = await _productRepository.GetActiveUnitsAsync(selected.Product.ProductId, CancellationToken.None);
            if (units.Count > 1)
            {
                UnitPricesText.Text = string.Join("   ", units.Select(unit =>
                    $"{unit.Label}: {FormatMoney(PdvProductRepository.ResolveUnitPrice(selected.Product, unit))}"));
            }
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException or TimeoutException)
        {
            // Consulta de unidades e opcional; o preco principal ja esta exibido.
        }
    }

    private async Task SearchAsync()
    {
        var query = PriceQueryTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            PriceProductNameText.Text = "Informe um codigo, codigo de barras ou nome de produto.";
            return;
        }

        try
        {
            var products = await _productRepository.SearchForSaleAsync(query, limit: 20, CancellationToken.None);
            _results.Clear();
            foreach (var product in products)
            {
                _results.Add(new UiProductSearchResult(product));
            }

            if (products.Count == 0)
            {
                PriceProductNameText.Text = "Produto nao encontrado no catalogo local.";
                PriceValueText.Text = FormatMoney(0);
                return;
            }

            PriceResultsGrid.SelectedIndex = 0;
            PriceQueryTextBox.SelectAll();
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException or TimeoutException)
        {
            PriceProductNameText.Text = "Erro ao consultar o catalogo local.";
            PriceValueText.Text = FormatMoney(0);
        }
    }
}

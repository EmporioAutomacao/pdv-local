using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using PdvLocal.Core;

namespace PdvLocal.App;

public partial class CustomerSearchWindow : Window
{
    private readonly PdvCustomerRepository _customerRepository;
    private readonly ObservableCollection<UiCustomerSearchResult> _results = [];

    public PdvCustomer? SelectedCustomer { get; private set; }

    internal CustomerSearchWindow(PdvCustomerRepository customerRepository)
    {
        _customerRepository = customerRepository;

        InitializeComponent();
        CustomerResultsGrid.ItemsSource = _results;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        CustomerQueryTextBox.Focus();
    }

    private async void CustomerQuerySearchButton_Click(object sender, RoutedEventArgs e)
    {
        await SearchAsync();
    }

    private async void CustomerQueryTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        await SearchAsync();
    }

    private void CustomerResultsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        ConfirmSelection();
    }

    private void CustomerResultsGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        ConfirmSelection();
    }

    private void SelectCustomerButton_Click(object sender, RoutedEventArgs e)
    {
        ConfirmSelection();
    }

    private async Task SearchAsync()
    {
        var query = CustomerQueryTextBox.Text.Trim();
        if (query.Length == 0)
        {
            CustomerSearchMessageText.Text = "Informe nome, documento ou codigo para buscar.";
            return;
        }

        try
        {
            var customers = await _customerRepository.SearchActiveAsync(query, limit: 30, CancellationToken.None);
            _results.Clear();
            foreach (var customer in customers)
            {
                _results.Add(new UiCustomerSearchResult(customer));
            }

            if (customers.Count == 0)
            {
                CustomerSearchMessageText.Text = "Nenhum cliente encontrado.";
                return;
            }

            CustomerSearchMessageText.Text = customers.Count == 1
                ? "1 cliente encontrado."
                : $"{customers.Count} clientes encontrados.";
            CustomerResultsGrid.SelectedIndex = 0;

            if (customers.Count == 1)
            {
                CustomerResultsGrid.Focus();
            }
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException or TimeoutException)
        {
            CustomerSearchMessageText.Text = "Falha ao consultar clientes no banco local.";
        }
    }

    private void ConfirmSelection()
    {
        if (CustomerResultsGrid.SelectedItem is not UiCustomerSearchResult selected)
        {
            CustomerSearchMessageText.Text = "Selecione um cliente na lista.";
            return;
        }

        SelectedCustomer = selected.Customer;
        DialogResult = true;
    }
}

internal sealed class UiCustomerSearchResult
{
    public UiCustomerSearchResult(PdvCustomer customer)
    {
        Customer = customer;
    }

    public PdvCustomer Customer { get; }
    public string CodeText => PdvUiFormatting.FirstNonEmpty(Customer.ExternalKey, "-");
    public string Name => Customer.Name;
    public string DocumentText => PdvUiFormatting.FirstNonEmpty(Customer.Document, "-");
}

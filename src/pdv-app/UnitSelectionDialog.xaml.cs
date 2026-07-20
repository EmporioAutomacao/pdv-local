using System.Globalization;
using System.Windows;
using System.Windows.Input;
using PdvLocal.Core;

namespace PdvLocal.App;

public partial class UnitSelectionDialog : Window
{
    private readonly IReadOnlyList<UiUnitOption> _options;

    public PdvProductUnit? SelectedUnit { get; private set; }

    internal UnitSelectionDialog(PdvProduct product, IReadOnlyList<PdvProductUnit> units)
    {
        _options = units
            .Select((unit, index) => new UiUnitOption(
                index + 1,
                unit,
                PdvProductRepository.ResolveUnitPrice(product, unit)))
            .ToList();

        InitializeComponent();
        ProductNameText.Text = product.Name;
        UnitsGrid.ItemsSource = _options;
        UnitsGrid.SelectedIndex = 0;
    }

    internal static PdvProductUnit? Request(Window owner, PdvProduct product, IReadOnlyList<PdvProductUnit> units)
    {
        var dialog = new UnitSelectionDialog(product, units) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.SelectedUnit : null;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        UnitsGrid.Focus();
    }

    private void UnitSelectionDialog_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter)
        {
            e.Handled = true;
            ConfirmSelection();
            return;
        }

        var optionNumber = e.Key switch
        {
            >= Key.D1 and <= Key.D9 => e.Key - Key.D0,
            >= Key.NumPad1 and <= Key.NumPad9 => e.Key - Key.NumPad0,
            _ => 0
        };

        if (optionNumber > 0 && optionNumber <= _options.Count)
        {
            e.Handled = true;
            UnitsGrid.SelectedIndex = optionNumber - 1;
            ConfirmSelection();
        }
    }

    private void UnitsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ConfirmSelection();
    }

    private void ConfirmUnitButton_Click(object sender, RoutedEventArgs e)
    {
        ConfirmSelection();
    }

    private void ConfirmSelection()
    {
        if (UnitsGrid.SelectedItem is not UiUnitOption selected)
        {
            return;
        }

        SelectedUnit = selected.Unit;
        DialogResult = true;
    }
}

internal sealed class UiUnitOption
{
    public UiUnitOption(int optionNumber, PdvProductUnit unit, decimal price)
    {
        OptionNumber = optionNumber;
        Unit = unit;
        Price = price;
    }

    public int OptionNumber { get; }
    public PdvProductUnit Unit { get; }
    public decimal Price { get; }
    public string Label => Unit.Label;
    public string Description => Unit.IsNative
        ? PdvUiFormatting.FirstNonEmpty(Unit.Name, "Unidade nativa")
        : Unit.Factor == 1
            ? PdvUiFormatting.FirstNonEmpty(Unit.Name, Unit.Label)
            : $"{PdvUiFormatting.FirstNonEmpty(Unit.Name, Unit.Label)} ({Unit.Factor.ToString("0.####", CultureInfo.GetCultureInfo("pt-BR"))} un)";
    public string PriceText => Price.ToString("C", CultureInfo.GetCultureInfo("pt-BR"));
}

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PdvLocal.Core;
using static PdvLocal.App.PdvUiFormatting;

namespace PdvLocal.App;

internal sealed record CashCloseReportData(
    Guid CashSessionId,
    string OperatorName,
    DateTimeOffset OpenedAtUtc,
    DateTimeOffset ClosedAt,
    PdvCashSessionSummary Summary,
    IReadOnlyList<PdvClosingCountEntry> Entries,
    IReadOnlyList<PdvCashMovementRecord> Movements);

public partial class CashCloseReportWindow : Window
{
    internal CashCloseReportWindow(CashCloseReportData data)
    {
        InitializeComponent();
        Populate(data);
    }

    private void Populate(CashCloseReportData data)
    {
        var header =
            $"Caixa {data.CashSessionId}\n" +
            $"Operador: {data.OperatorName}\n" +
            $"Abertura: {data.OpenedAtUtc.LocalDateTime:dd/MM/yyyy HH:mm}  Fechamento: {data.ClosedAt.LocalDateTime:dd/MM/yyyy HH:mm}";
        A4HeaderText.Text = header;
        ThermalHeaderText.Text = header;

        var summaryRows = new List<ReportLineRow>
        {
            new("Abertura (fundo de troco)", FormatMoney(data.Summary.OpeningAmount)),
            new($"Vendas ({data.Summary.SaleCount})", FormatMoney(data.Summary.TotalSalesAmount)),
            new("Dinheiro em vendas", FormatMoney(data.Summary.CashSalesAmount)),
            new("Suprimentos", FormatMoney(data.Summary.SupplyAmount)),
            new("Sangrias", FormatMoney(data.Summary.WithdrawalAmount)),
            new("Dinheiro esperado", FormatMoney(data.Summary.ExpectedCashAmount))
        };
        A4SummaryItems.ItemsSource = summaryRows;
        ThermalSummaryItems.ItemsSource = summaryRows;

        var speciesRows = data.Entries
            .Select(entry => new ReportSpeciesRow(entry))
            .ToList();
        A4SpeciesItems.ItemsSource = speciesRows;
        ThermalSpeciesItems.ItemsSource = speciesRows;

        var movementRows = data.Movements
            .Select(movement => new ReportMovementRow(movement))
            .ToList();
        A4MovementItems.ItemsSource = movementRows;
        ThermalMovementItems.ItemsSource = movementRows;
        A4NoMovementsText.Visibility = movementRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var totalDifference = data.Entries.Sum(entry => entry.Difference);
        var footer = $"Diferenca total: {FormatMoney(totalDifference)}";
        A4FooterText.Text = footer;
        ThermalFooterText.Text = footer;
    }

    private void FormatRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (A4Panel is null || ThermalPanel is null)
        {
            return;
        }

        var thermal = ThermalFormatRadio.IsChecked == true;
        A4Panel.Visibility = thermal ? Visibility.Collapsed : Visibility.Visible;
        ThermalPanel.Visibility = thermal ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PrintReportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var panel = ThermalFormatRadio.IsChecked == true ? ThermalPanel : A4Panel;
        panel.Measure(new Size(panel.Width, double.PositiveInfinity));
        panel.Arrange(new Rect(new Point(0, 0), panel.DesiredSize));
        dialog.PrintVisual(panel, "Fechamento de caixa PDV Local");
    }
}

internal sealed record ReportLineRow(string Label, string ValueText);

internal sealed class ReportSpeciesRow
{
    private static readonly CultureInfo BrazilianCulture = CultureInfo.GetCultureInfo("pt-BR");

    public ReportSpeciesRow(PdvClosingCountEntry entry)
    {
        Name = entry.SpeciesName;
        ExpectedText = entry.ExpectedAmount.ToString("C", BrazilianCulture);
        CountedText = entry.CountedAmount.ToString("C", BrazilianCulture);
        DifferenceText = entry.Difference.ToString("C", BrazilianCulture);
        DifferenceBrush = entry.Difference == 0
            ? Brushes.Black
            : entry.Difference < 0 ? Brushes.Firebrick : Brushes.DarkGoldenrod;
        ThermalLine =
            $"{Truncate(Name, 12),-12} {entry.ExpectedAmount,8:0.00} {entry.CountedAmount,8:0.00} {entry.Difference,7:0.00}";
    }

    public string Name { get; }
    public string ExpectedText { get; }
    public string CountedText { get; }
    public string DifferenceText { get; }
    public Brush DifferenceBrush { get; }
    public string ThermalLine { get; }

    private static string Truncate(string value, int max)
    {
        return value.Length <= max ? value : value[..max];
    }
}

internal sealed class ReportMovementRow
{
    private static readonly CultureInfo BrazilianCulture = CultureInfo.GetCultureInfo("pt-BR");

    public ReportMovementRow(PdvCashMovementRecord movement)
    {
        TimeText = movement.OccurredAtUtc.LocalDateTime.ToString("HH:mm");
        TypeText = movement.MovementType == "supply" ? "Suprimento" : "Sangria";
        AmountText = movement.Amount.ToString("C", BrazilianCulture);
        Observation = movement.Reason;
        ThermalLine = $"{TimeText} {TypeText} {movement.Amount:0.00}\n  {movement.Reason}";
    }

    public string TimeText { get; }
    public string TypeText { get; }
    public string AmountText { get; }
    public string Observation { get; }
    public string ThermalLine { get; }
}

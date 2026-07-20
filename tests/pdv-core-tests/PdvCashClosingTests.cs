using PdvLocal.Core;

namespace PdvLocal.Core.Tests;

public sealed class PdvCashClosingTests
{
    private static PdvCashSessionSummary BuildSummary(
        decimal openingAmount = 100m,
        decimal cashSales = 200m,
        decimal supply = 50m,
        decimal withdrawal = 30m,
        params PdvPaymentSpeciesSummary[] species)
    {
        var expectedCash = openingAmount + cashSales + supply - withdrawal;
        return new PdvCashSessionSummary(
            Guid.NewGuid(),
            openingAmount,
            cashSales,
            supply,
            withdrawal,
            expectedCash,
            species.Sum(item => item.Amount),
            species.Length,
            species);
    }

    [Fact]
    public void BuildClosingCounts_cash_species_expects_physical_cash()
    {
        var summary = BuildSummary(
            species: [new PdvPaymentSpeciesSummary("Dinheiro", "cash", 200m)]);
        var counted = new[] { new PdvClosingCount("Dinheiro", "cash", 315m) };

        var entries = PdvCashClosing.BuildClosingCounts(counted, summary);

        var entry = Assert.Single(entries);
        Assert.Equal(320m, entry.ExpectedAmount); // 100 + 200 + 50 - 30
        Assert.Equal(315m, entry.CountedAmount);
        Assert.Equal(-5m, entry.Difference);
    }

    [Fact]
    public void BuildClosingCounts_non_cash_species_expects_sales_amount()
    {
        var summary = BuildSummary(
            cashSales: 0,
            species:
            [
                new PdvPaymentSpeciesSummary("PIX", "pix", 80m),
                new PdvPaymentSpeciesSummary("Cartao de credito TEF", "card", 120m)
            ]);
        var counted = new[]
        {
            new PdvClosingCount("PIX", "pix", 80m),
            new PdvClosingCount("Cartao de credito TEF", "card", 100m)
        };

        var entries = PdvCashClosing.BuildClosingCounts(counted, summary);

        Assert.Equal(0m, entries.Single(e => e.SpeciesName == "PIX").Difference);
        Assert.Equal(-20m, entries.Single(e => e.SpeciesName == "Cartao de credito TEF").Difference);
    }

    [Fact]
    public void BuildClosingCounts_species_without_sales_expects_zero()
    {
        var summary = BuildSummary(cashSales: 0, species: []);
        var counted = new[] { new PdvClosingCount("PIX", "pix", 10m) };

        var entries = PdvCashClosing.BuildClosingCounts(counted, summary);

        var entry = Assert.Single(entries);
        Assert.Equal(0m, entry.ExpectedAmount);
        Assert.Equal(10m, entry.Difference);
    }

    [Fact]
    public void BuildClosingCounts_uncounted_species_with_sales_is_included_with_zero_counted()
    {
        var summary = BuildSummary(
            cashSales: 0,
            species: [new PdvPaymentSpeciesSummary("PIX", "pix", 90m)]);
        var counted = new[] { new PdvClosingCount("Dinheiro", "cash", 120m) };

        var entries = PdvCashClosing.BuildClosingCounts(counted, summary);

        Assert.Equal(2, entries.Count);
        var pix = entries.Single(e => e.SpeciesName == "PIX");
        Assert.Equal(0m, pix.CountedAmount);
        Assert.Equal(90m, pix.ExpectedAmount);
        Assert.Equal(-90m, pix.Difference);
    }

    [Fact]
    public void BuildClosingCounts_rejects_negative_and_unnamed_counts()
    {
        var summary = BuildSummary(species: []);

        Assert.Throws<ArgumentException>(() => PdvCashClosing.BuildClosingCounts(
            [new PdvClosingCount("Dinheiro", "cash", -1m)], summary));
        Assert.Throws<ArgumentException>(() => PdvCashClosing.BuildClosingCounts(
            [new PdvClosingCount("  ", "cash", 10m)], summary));
    }

    [Fact]
    public void BuildClosingCounts_second_cash_species_compares_against_zero()
    {
        var summary = BuildSummary(
            species: [new PdvPaymentSpeciesSummary("Dinheiro", "cash", 200m)]);
        var counted = new[]
        {
            new PdvClosingCount("Dinheiro", "cash", 320m),
            new PdvClosingCount("Dinheiro em moedas", "cash", 5m)
        };

        var entries = PdvCashClosing.BuildClosingCounts(counted, summary);

        Assert.Equal(320m, entries[0].ExpectedAmount);
        Assert.Equal(0m, entries[1].ExpectedAmount);
        Assert.Equal(5m, entries[1].Difference);
    }
}

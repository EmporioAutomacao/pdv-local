using PdvLocal.Core;

namespace PdvLocal.Core.Tests;

public sealed class PdvInstallmentCalculatorTests
{
    private static readonly DateOnly SaleDate = new(2026, 7, 19);

    [Fact]
    public void BuildPlan_splits_total_with_residue_on_last_installment()
    {
        var plan = PdvInstallmentCalculator.BuildPlan(100m, 3, 30, 30, SaleDate);

        Assert.Equal(3, plan.Count);
        Assert.Equal(33.33m, plan[0].Amount);
        Assert.Equal(33.33m, plan[1].Amount);
        Assert.Equal(33.34m, plan[2].Amount);
        Assert.Equal(100m, plan.Sum(entry => entry.Amount));
    }

    [Fact]
    public void BuildPlan_calculates_due_dates_from_condition()
    {
        var plan = PdvInstallmentCalculator.BuildPlan(300m, 3, 30, 30, SaleDate);

        Assert.Equal(new DateOnly(2026, 8, 18), plan[0].DueDate);
        Assert.Equal(new DateOnly(2026, 9, 17), plan[1].DueDate);
        Assert.Equal(new DateOnly(2026, 10, 17), plan[2].DueDate);
    }

    [Fact]
    public void BuildPlan_single_installment_due_today_when_first_due_zero()
    {
        var plan = PdvInstallmentCalculator.BuildPlan(50m, 1, 0, 30, SaleDate);

        var entry = Assert.Single(plan);
        Assert.Equal(1, entry.Number);
        Assert.Equal(SaleDate, entry.DueDate);
        Assert.Equal(50m, entry.Amount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public void BuildPlan_rejects_non_positive_total(decimal total)
    {
        Assert.Throws<ArgumentException>(() =>
            PdvInstallmentCalculator.BuildPlan(total, 2, 30, 30, SaleDate));
    }

    [Fact]
    public void BuildPlan_rejects_invalid_installments_and_days()
    {
        Assert.Throws<ArgumentException>(() =>
            PdvInstallmentCalculator.BuildPlan(100m, 0, 30, 30, SaleDate));
        Assert.Throws<ArgumentException>(() =>
            PdvInstallmentCalculator.BuildPlan(100m, 2, -1, 30, SaleDate));
    }

    [Fact]
    public void ValidatePlan_accepts_manually_edited_dates()
    {
        var plan = new[]
        {
            new PdvInstallmentPlanEntry(1, new DateOnly(2026, 8, 5), 40m),
            new PdvInstallmentPlanEntry(2, new DateOnly(2026, 9, 10), 60m)
        };

        PdvInstallmentCalculator.ValidatePlan(plan, 100m);
    }

    [Fact]
    public void ValidatePlan_rejects_sum_mismatch()
    {
        var plan = new[]
        {
            new PdvInstallmentPlanEntry(1, new DateOnly(2026, 8, 5), 40m),
            new PdvInstallmentPlanEntry(2, new DateOnly(2026, 9, 5), 50m)
        };

        Assert.Throws<ArgumentException>(() => PdvInstallmentCalculator.ValidatePlan(plan, 100m));
    }

    [Fact]
    public void ValidatePlan_rejects_decreasing_due_dates()
    {
        var plan = new[]
        {
            new PdvInstallmentPlanEntry(1, new DateOnly(2026, 9, 5), 50m),
            new PdvInstallmentPlanEntry(2, new DateOnly(2026, 8, 5), 50m)
        };

        Assert.Throws<ArgumentException>(() => PdvInstallmentCalculator.ValidatePlan(plan, 100m));
    }

    [Fact]
    public void ValidatePlan_rejects_wrong_numbering_and_empty_plan()
    {
        var wrongNumbering = new[]
        {
            new PdvInstallmentPlanEntry(2, new DateOnly(2026, 8, 5), 100m)
        };

        Assert.Throws<ArgumentException>(() => PdvInstallmentCalculator.ValidatePlan(wrongNumbering, 100m));
        Assert.Throws<ArgumentException>(() => PdvInstallmentCalculator.ValidatePlan([], 100m));
    }

    [Theory]
    [InlineData(1, 0, false)]
    [InlineData(1, 30, true)]
    [InlineData(2, 0, true)]
    [InlineData(3, 30, true)]
    public void RequiresRegisteredCustomer_detects_credit_conditions(int installments, int firstDueDays, bool expected)
    {
        Assert.Equal(expected, PdvValidation.RequiresRegisteredCustomer(installments, firstDueDays));
    }
}

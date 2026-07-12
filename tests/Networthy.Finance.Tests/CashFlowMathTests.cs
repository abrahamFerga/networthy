namespace Networthy.Finance.Tests;

/// <summary>
/// The Cash flow tab's bucketing contract (issue #46): a fixed 12-month window, both
/// directions in every month (zero-filled), oldest first — the chart's x-axis and its
/// income/expense pairing depend on the rows being complete and ordered, not sparse.
/// No activity at all yields no rows, so the chart shows its empty state instead of
/// twelve silent zero months.
/// </summary>
public class CashFlowMathTests
{
    private static readonly DateOnly Today = new(2026, 7, 11);

    [Fact]
    public void One_transaction_still_emits_both_directions_for_every_month_zero_filled()
    {
        var rows = CashFlowMath.MonthlyTotals(
            [(new DateOnly(2026, 7, 1), "income", 2500m)], Today);

        Assert.Equal(24, rows.Count); // 12 months × income + expense — quiet months included
        Assert.Equal("2025-08", rows[0].Month);
        Assert.Equal("2026-07", rows[^1].Month);
        // Pairing within each month: income first, then expense.
        Assert.Equal("income", rows[0].Direction);
        Assert.Equal("expense", rows[1].Direction);
        Assert.Equal(2500m, rows.Sum(r => r.Amount)); // everything else is an explicit zero
    }

    [Fact]
    public void Sums_land_in_their_own_month_and_direction_bucket()
    {
        var rows = CashFlowMath.MonthlyTotals(
        [
            (new DateOnly(2026, 7, 1), "income", 2500m),
            (new DateOnly(2026, 7, 9), "expense", 40m),
            (new DateOnly(2026, 7, 10), "expense", 60m),
            (new DateOnly(2026, 6, 15), "expense", 300m),
        ], Today);

        Assert.Equal(2500m, rows.Single(r => r.Month == "2026-07" && r.Direction == "income").Amount);
        Assert.Equal(100m, rows.Single(r => r.Month == "2026-07" && r.Direction == "expense").Amount);
        Assert.Equal(300m, rows.Single(r => r.Month == "2026-06" && r.Direction == "expense").Amount);
        Assert.Equal(0m, rows.Single(r => r.Month == "2026-06" && r.Direction == "income").Amount);
    }

    [Fact]
    public void No_activity_in_the_window_means_no_rows_not_twelve_silent_months()
    {
        Assert.Empty(CashFlowMath.MonthlyTotals([], Today));
        Assert.Empty(CashFlowMath.MonthlyTotals(
        [
            (new DateOnly(2025, 7, 31), "expense", 999m), // one day before the window
            (new DateOnly(2026, 8, 1), "income", 999m),   // a next-month future entry
        ], Today));
    }

    [Fact]
    public void Unknown_directions_are_ignored_not_miscounted()
    {
        Assert.Empty(CashFlowMath.MonthlyTotals(
            [(new DateOnly(2026, 7, 1), "transfer", 500m)], Today));
    }

    [Fact]
    public void Months_are_ordered_oldest_first_for_the_chart_axis()
    {
        var rows = CashFlowMath.MonthlyTotals(
            [(new DateOnly(2026, 2, 3), "expense", 10m)], Today);
        var months = rows.Select(r => r.Month).Distinct().ToList();

        Assert.Equal(12, months.Count);
        Assert.Equal(months.OrderBy(m => m), months);
    }
}

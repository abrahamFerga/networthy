using Networthy.Finance;
using Xunit;

namespace Networthy.Finance.Tests;

/// <summary>
/// The hero number's contract (ADR-0008): one formula, computed server-side, conservative and
/// deterministic. These tests pin the properties the dashboard and (epic 11) the chat
/// explanation both depend on.
/// </summary>
public class SafeToSpendMathTests
{
    [Fact]
    public void Sums_what_is_left_across_budgets()
    {
        var result = SafeToSpendMath.Compute([(400m, 150m), (200m, 50m)]);

        Assert.NotNull(result);
        Assert.Equal(400m, result!.Amount); // 250 + 150
        Assert.Equal(600m, result.TotalTarget);
        Assert.Equal(200m, result.TotalSpent);
        Assert.Equal(2, result.BudgetCount);
    }

    [Fact]
    public void An_over_budget_category_eats_headroom_so_the_figure_matches_its_own_totals()
    {
        // Reverses the earlier per-category clamp (issue #149). Summing clamped remainders while
        // reporting unclamped totals shipped a hero number its own totals could not produce:
        // this case read 300 beside totalTarget 600 / totalSpent 400. Real overspend is money
        // already gone, so it must reduce the figure — that is what makes it conservative.
        var result = SafeToSpendMath.Compute([(400m, 100m), (200m, 300m)]);

        Assert.Equal(200m, result!.Amount); // 600 − 400, not max(0,300) + max(0,-100)
        Assert.Equal(600m, result.TotalTarget);
        Assert.Equal(400m, result.TotalSpent);
    }

    [Theory]
    // no budget over            one over                       all over
    [InlineData(400, 150, 200, 50)]
    [InlineData(400, 100, 200, 300)]
    [InlineData(100, 250, 200, 300)]
    public void The_figure_is_always_derivable_from_the_totals_shipped_beside_it(
        decimal targetA, decimal spentA, decimal targetB, decimal spentB)
    {
        // The invariant #149 is really about: whatever the formula, a reader holding only the
        // payload must be able to reproduce the hero number. SPEC.md:126 sells this figure as the
        // explainable alternative to a black-box formula, so an unreproducible one is a defect.
        var result = SafeToSpendMath.Compute([(targetA, spentA), (targetB, spentB)]);

        Assert.NotNull(result);
        Assert.Equal(Math.Max(0m, result!.TotalTarget - result.TotalSpent), result.Amount);
    }

    [Fact]
    public void Everything_over_budget_reads_zero_not_negative()
    {
        var result = SafeToSpendMath.Compute([(100m, 250m)]);

        Assert.Equal(0m, result!.Amount);
    }

    [Fact]
    public void No_budgets_means_no_number_not_a_fabricated_zero()
    {
        // Null tells the UI to render guidance; 0 would falsely read as "you can't spend".
        Assert.Null(SafeToSpendMath.Compute([]));
    }
}

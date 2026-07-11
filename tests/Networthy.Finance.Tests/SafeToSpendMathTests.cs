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
    public void An_over_budget_category_contributes_zero_never_negative()
    {
        // Dining is 100 over; that overage must not eat Groceries' real headroom.
        var result = SafeToSpendMath.Compute([(400m, 100m), (200m, 300m)]);

        Assert.Equal(300m, result!.Amount); // max(0, 300) + max(0, -100) = 300
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

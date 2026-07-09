using Networthy.Finance;
using Networthy.Finance.Persistence;

namespace Networthy.Finance.Tests;

/// <summary>Budget pure logic: status math, month parsing, and the rollover plan.</summary>
public sealed class BudgetMathTests
{
    [Theory]
    [InlineData(400, 250, 150, false)]
    [InlineData(400, 400, 0, false)]   // exactly on target is not over
    [InlineData(400, 401, -1, true)]
    public void Status_RemainingAndOverFlag(double target, double spent, double remaining, bool over)
    {
        var status = BudgetMath.Status((decimal)target, (decimal)spent);
        Assert.Equal(((decimal)remaining, over), (status.Remaining, status.Over));
    }

    [Fact]
    public void TryParseMonth_ParsesYyyyMm_DefaultsToCurrentMonth_RejectsGarbage()
    {
        Assert.True(BudgetMath.TryParseMonth("2026-07", out var period));
        Assert.Equal(new DateOnly(2026, 7, 1), period);

        Assert.True(BudgetMath.TryParseMonth(null, out var current));
        Assert.Equal(1, current.Day);

        Assert.False(BudgetMath.TryParseMonth("July 2026", out _));
    }

    [Fact]
    public void RolloverPlan_CopiesOnlyWhatIsMissing()
    {
        var groceries = Guid.NewGuid();
        var dining = Guid.NewGuid();
        var previous = new List<Budget>
        {
            new() { CategoryId = groceries, CurrencyCode = "USD", TargetAmount = 400m },
            new() { CategoryId = dining, CurrencyCode = "USD", TargetAmount = 200m },
            new() { CategoryId = groceries, CurrencyCode = "MXN", TargetAmount = 3000m },
        };
        // This month already has a groceries/USD target (user set it manually — don't clobber).
        var present = new HashSet<(Guid, string)> { (groceries, "USD") };

        var plan = BudgetMath.RolloverPlan(previous, present);

        Assert.Equal(2, plan.Count);
        Assert.DoesNotContain(plan, b => b.CategoryId == groceries && b.CurrencyCode == "USD");
        Assert.Contains(plan, b => b.CategoryId == dining);
        Assert.Contains(plan, b => b.CurrencyCode == "MXN");
    }
}

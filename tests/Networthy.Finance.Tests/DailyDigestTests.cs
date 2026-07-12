using Networthy.Finance;
using Networthy.Finance.Persistence;
using Xunit;

namespace Networthy.Finance.Tests;

/// <summary>
/// The digest's pure composition (issue #48): what counts as over-budget, what counts as a NEWLY
/// detected recurring charge, and the batching titles. The handler around it only fetches rows
/// and sends — everything decision-shaped is pinned here without a database.
/// </summary>
public class DailyDigestTests
{
    private static readonly Guid Groceries = Guid.NewGuid();
    private static readonly Guid Dining = Guid.NewGuid();

    private static readonly Dictionary<Guid, string> Names = new()
    {
        [Groceries] = "Groceries",
        [Dining] = "Dining out",
    };

    private static Budget MakeBudget(Guid categoryId, decimal target, string currency = "USD") => new()
    {
        TenantId = Guid.NewGuid(),
        CategoryId = categoryId,
        TargetAmount = target,
        CurrencyCode = currency,
        PeriodMonth = new DateOnly(2026, 7, 1),
    };

    private static Transaction Spend(Guid categoryId, decimal amount, string currency = "USD") => new()
    {
        TenantId = Guid.NewGuid(),
        AccountId = Guid.NewGuid(),
        CategoryId = categoryId,
        Amount = amount,
        CurrencyCode = currency,
        Direction = "expense",
        Description = "test",
        Source = "manual",
        OccurredOn = new DateOnly(2026, 7, 10),
    };

    [Fact]
    public void OverBudget_FlagsOnlyExceededCategories_WorstFirst()
    {
        var budgets = new[] { MakeBudget(Groceries, 400), MakeBudget(Dining, 100) };
        var spent = new[] { Spend(Groceries, 410), Spend(Dining, 250) };

        var lines = DailyDigest.OverBudget(budgets, spent, Names, "USD");

        Assert.Equal(2, lines.Count);
        Assert.Equal("Dining out", lines[0].CategoryName); // over by 150 sorts above over by 10
        Assert.Equal(150, lines[0].OverBy);
        Assert.Equal("Groceries", lines[1].CategoryName);
        Assert.Equal(10, lines[1].OverBy);
    }

    [Fact]
    public void OverBudget_AtExactlyTarget_IsNotOver()
    {
        var lines = DailyDigest.OverBudget(
            [MakeBudget(Groceries, 400)], [Spend(Groceries, 400)], Names, "USD");
        Assert.Empty(lines);
    }

    [Fact]
    public void OverBudget_IgnoresOtherCurrencies_BothSides()
    {
        // An MXN budget never reads against the USD digest, and MXN spending never counts
        // toward a USD budget — same single-currency honesty as the dashboard.
        var lines = DailyDigest.OverBudget(
            [MakeBudget(Groceries, 400, "MXN"), MakeBudget(Dining, 100)],
            [Spend(Groceries, 900, "MXN"), Spend(Dining, 90, "MXN"), Spend(Dining, 90)],
            Names, "USD");
        Assert.Empty(lines);
    }

    [Fact]
    public void NewlyDetected_IsThresholdCrossing_WithinFreshnessWindow()
    {
        var today = new DateOnly(2026, 7, 12);
        RecurringCharge Charge(int occurrences, DateOnly lastSeen) =>
            new("spotify", "Spotify", "monthly", 199, 199, lastSeen, lastSeen.AddMonths(1), occurrences);

        var charges = new[]
        {
            Charge(3, today),                 // just crossed the minimum, fresh -> new
            Charge(3, today.AddDays(-2)),     // edge of the freshness window -> new
            Charge(3, today.AddDays(-3)),     // crossed a while ago; its digest already ran
            Charge(4, today),                 // long-known charge with a fresh occurrence -> not new
        };

        var fresh = DailyDigest.NewlyDetected(charges, today);

        Assert.Equal(2, fresh.Count);
        Assert.All(fresh, c => Assert.Equal(3, c.Occurrences));
    }

    [Fact]
    public void Titles_CountCorrectly()
    {
        Assert.Equal("1 category is over budget", DailyDigest.OverBudgetTitle(1));
        Assert.Equal("3 categories are over budget", DailyDigest.OverBudgetTitle(3));
        Assert.Equal("New recurring charge detected", DailyDigest.NewlyDetectedTitle(1));
        Assert.Equal("2 new recurring charges detected", DailyDigest.NewlyDetectedTitle(2));
    }
}

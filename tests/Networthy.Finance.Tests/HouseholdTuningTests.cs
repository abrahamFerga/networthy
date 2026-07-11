using Networthy.Finance;
using Networthy.Finance.Persistence;
using Xunit;

namespace Networthy.Finance.Tests;

/// <summary>
/// The household's own tuning knobs: the health assessment's thresholds come from settings
/// instead of constants, and multi-currency net worth combines only through user-saved rates —
/// a currency without a rate is reported separately, never guessed at.
/// </summary>
public sealed class HouseholdTuningTests
{
    private static Account Debt(string name, decimal owed, decimal? apr) => new()
    {
        Name = name,
        Type = "credit",
        CurrencyCode = "USD",
        CachedBalance = -owed,
        InterestRateApr = apr,
    };

    [Fact]
    public void HighAprThreshold_IsTheHouseholds_NotAConstant()
    {
        var accounts = new[] { Debt("Card", 1000m, 20m) };

        // Default threshold (8%): a 20% card is flagged for avalanche paydown.
        var defaults = FinancialHealthMath.Assess(accounts, 0, 0, 0, 0);
        Assert.Contains(defaults.Suggestions, s => s.Contains("avalanche"));

        // A household that set the bar at 30% gets no avalanche flag for the same card.
        var tuned = FinancialHealthMath.Assess(accounts, 0, 0, 0, 0, highAprThreshold: 30m);
        Assert.DoesNotContain(tuned.Suggestions, s => s.Contains("avalanche"));
    }

    [Fact]
    public void EmergencyFloor_IsTheHouseholds_NotAConstant()
    {
        // Liquid ≈ 4 months of expenses (1200 liquid vs 300/month average).
        var accounts = new[]
        {
            new Account { Name = "Checking", Type = "checking", CurrencyCode = "USD", CachedBalance = 1200m },
        };

        // Default floor (3 months): 4 months of coverage raises no flag.
        var defaults = FinancialHealthMath.Assess(accounts, income90: 1000m, expense90: 900m, 0, 0);
        Assert.DoesNotContain(defaults.Suggestions, s => s.Contains("Emergency fund"));

        // A 6-month household is told it's short.
        var tuned = FinancialHealthMath.Assess(accounts, income90: 1000m, expense90: 900m, 0, 0,
            emergencyFloorMonths: 6m);
        Assert.Contains(tuned.Suggestions, s => s.Contains("Emergency fund"));
    }

    [Fact]
    public void Combine_ConvertsOnlyThroughSavedRates_AndReportsTheRest()
    {
        var totals = new[] { ("EUR", 100m), ("MXN", 50_000m), ("USD", 1_000m) };
        var rates = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["USD"] = 17m };

        var (total, converted, unconvertible) = NetWorthMath.Combine(totals, "MXN", rates);

        // MXN counts as-is, USD through the saved rate, EUR is excluded — not guessed.
        Assert.Equal(50_000m + 17_000m, total);
        var usd = Assert.Single(converted);
        Assert.Equal(("USD", 1_000m, 17_000m), usd);
        var eur = Assert.Single(unconvertible);
        Assert.Equal(("EUR", 100m), eur);
    }

    [Fact]
    public void Combine_WithAllRatesSaved_LeavesNothingBehind()
    {
        var totals = new[] { ("MXN", 10_000m), ("USD", 100m) };
        var rates = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["USD"] = 17.5m };

        var (total, converted, unconvertible) = NetWorthMath.Combine(totals, "MXN", rates);

        Assert.Equal(11_750m, total);
        Assert.Single(converted);
        Assert.Empty(unconvertible);
    }
}

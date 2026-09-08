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
    /// <summary>A household that has never left its own currency, which is most of them.</summary>
    private const string Usd = "USD";

    private static readonly Dictionary<string, decimal> NoRates = new(StringComparer.OrdinalIgnoreCase);

    private static SafeToSpendMath.SafeToSpend? Compute(
        params (decimal Target, decimal Spent)[] budgets) =>
        SafeToSpendMath.Compute([.. budgets.Select(b => (b.Target, b.Spent, Usd))], Usd, NoRates);

    [Fact]
    public void Sums_what_is_left_across_budgets()
    {
        var result = Compute((400m, 150m), (200m, 50m));

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
        var result = Compute((400m, 100m), (200m, 300m));

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
        var result = Compute((targetA, spentA), (targetB, spentB));

        Assert.NotNull(result);
        Assert.Equal(Math.Max(0m, result!.TotalTarget - result.TotalSpent), result.Amount);
    }

    [Fact]
    public void Everything_over_budget_reads_zero_not_negative()
    {
        var result = Compute((100m, 250m));

        Assert.Equal(0m, result!.Amount);
    }

    [Fact]
    public void No_budgets_means_no_number_not_a_fabricated_zero()
    {
        // Null tells the UI to render guidance; 0 would falsely read as "you can't spend".
        Assert.Null(SafeToSpendMath.Compute([], Usd, NoRates));
    }

    // ── Currency (issue #214) ─────────────────────────────────────────────────
    // The caller used to filter budgets down to the household default before calling in, so a
    // budget in any other currency simply disappeared — and a household whose only budget was
    // foreign got a bare null, which the Overview tab renders as "No budgets yet this month"
    // beside a card listing that budget. The policy adopted here is NetWorthMath.Combine's: the
    // household's own rate converts, an unrated currency is never guessed at, and what was left
    // out is always reported.

    [Fact]
    public void A_foreign_budget_is_converted_through_the_households_own_saved_rate()
    {
        var result = SafeToSpendMath.Compute(
            [(400m, 82.15m, "USD")], "MXN", new Dictionary<string, decimal> { ["USD"] = 20m });

        // 400 × 20 = 8,000 targeted; 82.15 × 20 = 1,643.00 spent.
        Assert.Equal(8_000m, result!.TotalTarget);
        Assert.Equal(1_643m, result.TotalSpent);
        Assert.Equal(6_357m, result.Amount);
        Assert.Equal(1, result.BudgetCount);

        var converted = Assert.Single(result.Converted);
        Assert.Equal("USD", converted.CurrencyCode);
        Assert.Equal(400m, converted.Target);
        Assert.Equal(82.15m, converted.Spent);
        Assert.Equal(8_000m, converted.ConvertedTarget);
        Assert.Equal(1_643m, converted.ConvertedSpent);
        Assert.Equal(20m, converted.RateToDefault);
        Assert.Empty(result.Excluded);
    }

    [Fact]
    public void An_unrated_currency_is_reported_rather_than_guessed_at_or_dropped()
    {
        var result = SafeToSpendMath.Compute([(400m, 82.15m, "USD")], "MXN", NoRates);

        // Not null — that is the whole defect. A record with a null Amount says "there are budgets
        // and no honest combined number", which is a different sentence from "there are no budgets".
        Assert.NotNull(result);
        Assert.Null(result!.Amount);
        Assert.Equal(0, result.BudgetCount);

        var excluded = Assert.Single(result.Excluded);
        Assert.Equal("USD", excluded.CurrencyCode);
        Assert.Equal(400m, excluded.Target);
        Assert.Equal(82.15m, excluded.Spent);
        Assert.Equal(1, excluded.BudgetCount);
    }

    [Fact]
    public void A_partly_convertible_household_still_ships_a_reproducible_figure()
    {
        // One budget in the default, one converted, one unrated — the case where a caller is most
        // tempted to ship a total that its own lists cannot reproduce.
        var result = SafeToSpendMath.Compute(
            [(500m, 100m, "MXN"), (400m, 82.15m, "USD"), (300m, 50m, "GBP")],
            "MXN",
            new Dictionary<string, decimal> { ["USD"] = 20m });

        Assert.Equal(8_500m, result!.TotalTarget);   // 500 + 8,000
        Assert.Equal(1_743m, result.TotalSpent);     // 100 + 1,643
        Assert.Equal(Math.Max(0m, result.TotalTarget - result.TotalSpent), result.Amount);
        Assert.Equal(2, result.BudgetCount);         // the GBP budget is NOT counted as included

        Assert.Equal("USD", Assert.Single(result.Converted).CurrencyCode);
        var excluded = Assert.Single(result.Excluded);
        Assert.Equal("GBP", excluded.CurrencyCode);
        Assert.Equal(300m, excluded.Target);
    }

    [Fact]
    public void Currency_matching_is_case_insensitive_so_a_lowercased_code_is_not_stranded()
    {
        // Codes arrive from tools, imports and hand-typed forms; a casing mismatch silently
        // excluding a budget would reproduce #214 through a different door.
        var result = SafeToSpendMath.Compute([(400m, 100m, "usd")], "USD", NoRates);

        Assert.Equal(300m, result!.Amount);
        Assert.Equal(1, result.BudgetCount);
        Assert.Empty(result.Excluded);
    }
}

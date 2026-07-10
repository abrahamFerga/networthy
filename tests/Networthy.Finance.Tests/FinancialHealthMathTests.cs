using Networthy.Finance;
using Networthy.Finance.Persistence;

namespace Networthy.Finance.Tests;

public class FinancialHealthMathTests
{
    private static Account Acct(string name, string type, decimal balance, decimal? apr = null) => new()
    {
        Name = name, Type = type, CurrencyCode = "USD", CachedBalance = balance, InterestRateApr = apr,
    };

    [Fact]
    public void Debts_are_priced_and_ordered_by_apr_descending_avalanche()
    {
        var snapshot = FinancialHealthMath.Assess(
            [
                Acct("Checking", "checking", 5000),
                Acct("Mortgage", "loan", -200_000, 6.25m),
                Acct("Visa", "credit", -3_000, 23.99m),
            ],
            income90: 15000, expense90: 12000, budgetCount: 1, overBudgetCount: 0);

        Assert.Equal(2, snapshot.Debts.Count);
        Assert.Equal("Visa", snapshot.Debts[0].Name); // costliest APR first
        // 200,000 * 6.25% / 12 = 1,041.67 ; 3,000 * 23.99% / 12 = 59.98
        Assert.Equal(1041.67m, snapshot.Debts[1].MonthlyInterest);
        Assert.Equal(59.98m, snapshot.Debts[0].MonthlyInterest);
        Assert.Equal(1101.65m, snapshot.MonthlyInterestCost);
        Assert.Equal(-198_000, snapshot.NetWorth); // straight sum, debts negative
    }

    [Fact]
    public void Weighted_apr_ignores_unpriced_debt_instead_of_diluting_it()
    {
        var snapshot = FinancialHealthMath.Assess(
            [Acct("Card", "credit", -1000, 20m), Acct("IOU", "loan", -9000)],
            income90: 0, expense90: 0, budgetCount: 0, overBudgetCount: 0);

        Assert.Equal(20m, snapshot.WeightedApr); // the unpriced 9k doesn't fake a lower average
        Assert.Contains(snapshot.Suggestions, s => s.Contains("'IOU'") && s.Contains("interest rate"));
    }

    [Fact]
    public void Suggestions_fire_only_on_their_thresholds()
    {
        // Healthy household: no debt, 6 months of buffer, 33% savings rate, budgets on track.
        var healthy = FinancialHealthMath.Assess(
            [Acct("Checking", "checking", 24_000)],
            income90: 18_000, expense90: 12_000, budgetCount: 2, overBudgetCount: 0);
        Assert.Empty(healthy.Suggestions);

        // Stressed household: high-APR card, thin buffer, negative savings rate, budget blown.
        var stressed = FinancialHealthMath.Assess(
            [Acct("Checking", "checking", 1_000), Acct("Visa", "credit", -8_000, 24m)],
            income90: 9_000, expense90: 10_500, budgetCount: 1, overBudgetCount: 1);
        Assert.Contains(stressed.Suggestions, s => s.Contains("avalanche"));
        Assert.Contains(stressed.Suggestions, s => s.Contains("Emergency fund"));
        Assert.Contains(stressed.Suggestions, s => s.Contains("exceeded income"));
        Assert.Contains(stressed.Suggestions, s => s.Contains("budget(s) are over"));
    }

    [Fact]
    public void Emergency_fund_months_come_from_actual_expense_history()
    {
        var snapshot = FinancialHealthMath.Assess(
            [Acct("Checking", "checking", 6_000)],
            income90: 9_000, expense90: 6_000, budgetCount: 0, overBudgetCount: 0);

        Assert.Equal(3m, snapshot.EmergencyFundMonths); // 6,000 liquid / (6,000/3 per month)
        // Exactly at the floor — no emergency-fund flag.
        Assert.DoesNotContain(snapshot.Suggestions, s => s.Contains("Emergency fund"));
    }

    [Fact]
    public void Loan_type_normalizes_and_counts_as_debt()
    {
        Assert.Equal("loan", Account.NormalizeType("Mortgage"));
        Assert.Equal("loan", Account.NormalizeType("student loan"));
        Assert.Equal("loan", Account.NormalizeType("HELOC"));
        Assert.True(Acct("m", "loan", -1).IsDebt);
        Assert.False(Acct("s", "savings", 1).IsDebt);
    }
}

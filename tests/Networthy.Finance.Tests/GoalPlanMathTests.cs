using Networthy.Finance;
using Networthy.Finance.Persistence;

namespace Networthy.Finance.Tests;

public class GoalPlanMathTests
{
    [Fact]
    public void Flat_saving_is_remaining_over_months()
    {
        // "$3,000 for vacations in 10 months, $500 already put aside" → $250/month.
        Assert.Equal(250m, GoalPlanMath.RequiredMonthlyContribution(3000, 500, 10, annualReturnPct: 0));
    }

    [Fact]
    public void Invested_goal_uses_future_value_annuity_math()
    {
        // The classic: 100,000 in 20 years at 7% nominal, nothing saved → 191.97/month
        // (i = 0.07/12; PMT = FV·i / ((1+i)^240 − 1)).
        var payment = GoalPlanMath.RequiredMonthlyContribution(100_000, 0, 240, 7);
        Assert.Equal(191.97m, payment); // 100000·i/((1+i)^240−1), i=0.07/12 → 191.9656

        // Growth on the existing balance counts too: 20k already invested grows to ~80.8k
        // over the 20 years, so contributions only need to cover the remaining ~19.2k of FV.
        var withBase = GoalPlanMath.RequiredMonthlyContribution(100_000, 20_000, 240, 7);
        Assert.InRange(withBase, 36m, 38m);
    }

    [Fact]
    public void A_return_assumption_never_produces_a_negative_contribution()
    {
        // Saved amount already grows past the target → 0, not a negative "withdraw".
        Assert.Equal(0m, GoalPlanMath.RequiredMonthlyContribution(10_000, 9_000, 240, 7));
    }

    [Theory]
    [InlineData("monthly", 260.0)]
    [InlineData("semimonthly", 130.0)]
    [InlineData("biweekly", 120.0)]   // 26 pays/year, not 24 — the classic payroll trap
    [InlineData("weekly", 60.0)]
    public void Per_paycheck_respects_real_pay_counts(string cadence, double expected) =>
        Assert.Equal((decimal)expected, GoalPlanMath.PerPaycheck(260m, cadence));

    [Fact]
    public void Cadence_normalization_and_monthly_equivalents()
    {
        Assert.Equal("biweekly", IncomeSource.NormalizeCadence("every two weeks"));
        Assert.Equal("semimonthly", IncomeSource.NormalizeCadence("twice a month"));
        Assert.Null(IncomeSource.NormalizeCadence("quarterly"));

        var biweekly = new IncomeSource { Name = "p", CurrencyCode = "USD", Cadence = "biweekly", Amount = 2500 };
        Assert.Equal(5416.67m, Math.Round(biweekly.MonthlyEquivalent, 2)); // 2500·26/12
    }

    [Fact]
    public void MonthsBetween_floors_partial_months()
    {
        Assert.Equal(12, GoalPlanMath.MonthsBetween(new DateOnly(2026, 7, 10), new DateOnly(2027, 7, 10)));
        Assert.Equal(11, GoalPlanMath.MonthsBetween(new DateOnly(2026, 7, 10), new DateOnly(2027, 7, 9)));
        Assert.Equal(0, GoalPlanMath.MonthsBetween(new DateOnly(2026, 7, 10), new DateOnly(2026, 7, 1)));
    }
}

using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Networthy.Finance;
using Xunit;

namespace Networthy.IntegrationTests;

/// <summary>
/// The planning layer end to end: declared income cadences feed per-paycheck goal plans; the
/// vacation case (flat saving, where-to-save) and the invested case (user-supplied assumed
/// return, no-growth figure shown beside it) both answer with math, framed as math.
/// </summary>
[Collection("api")]
public sealed class GoalPlanningTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task VacationGoal_PlansFlatContributions_PerPaycheck_AndSaysWhere()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var services = scope.ServiceProvider;

        await services.GetRequiredService<AccountTools>().CreateAccount("Plan Savings", "savings", "USD", 0);
        Assert.Contains("biweekly", await services.GetRequiredService<IncomeSourceTools>()
            .SetIncomeSource("Plan Payroll", 2500, "every two weeks", accountName: null));

        var deadline = DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(10);
        await services.GetRequiredService<GoalTools>().SetGoal("Vacation 3k", 3000, targetDate: deadline.ToString("yyyy-MM-dd"));

        var plan = await services.GetRequiredService<GoalPlanTools>().GetGoalPlan("Vacation 3k");
        Assert.Contains("300.00/month", plan);                       // 3000 over 10 whole months
        Assert.Contains("per biweekly paycheck ('Plan Payroll')", plan);
        Assert.Contains("138.46", plan);                             // 300·12/26
        Assert.Contains("'Plan Savings'", plan);                     // where: their own savings account
        Assert.Contains("not financial advice", plan);
    }

    [Fact]
    public async Task InvestedGoal_UsesTheHouseholdsOwnReturnAssumption_AndShowsItsWeight()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var services = scope.ServiceProvider;

        var deadline = DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(240);
        Assert.Contains("Goal '100k by 55'", await services.GetRequiredService<GoalTools>()
            .SetGoal("100k by 55", 100_000, targetDate: deadline.ToString("yyyy-MM-dd"), expectedAnnualReturnPct: 7));

        var plan = await services.GetRequiredService<GoalPlanTools>().GetGoalPlan("100k by 55");
        Assert.Contains("Assumed annual return: 7%", plan);
        Assert.Contains("YOUR assumption, not a promise", plan);
        Assert.Contains("191.97", plan);                             // the annuity answer (i=7%/12, n=240)
        Assert.Contains("Without growth (0%): 416.67/month", plan);  // 100k/240 — the assumption's weight
        Assert.Contains("brokerage", plan);                          // where, for invested goals
    }

    [Fact]
    public async Task GoalWithoutADate_GetsAnHonestNudge_NotAFakePlan()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var services = scope.ServiceProvider;

        await services.GetRequiredService<GoalTools>().SetGoal("Someday fund", 1000);
        var plan = await services.GetRequiredService<GoalPlanTools>().GetGoalPlan("Someday fund");
        Assert.Contains("no target date", plan);
        Assert.Contains("by age 55", plan); // the age-conversion hint
    }

    [Fact]
    public async Task IncomeTab_ListsSchedules_WithMonthlyEquivalents()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        await scope.ServiceProvider.GetRequiredService<IncomeSourceTools>()
            .SetIncomeSource("Tab Salary", 3000, "semimonthly");

        using var client = fixture.AdminClient();
        var rows = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/finance/income-sources");
        var salary = rows.EnumerateArray().Single(r => r.GetProperty("name").GetString() == "Tab Salary");
        Assert.Equal("semimonthly", salary.GetProperty("cadence").GetString());
        Assert.Equal(6000, salary.GetProperty("monthlyEquivalent").GetDecimal()); // 3000·24/12
    }
}

using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Networthy.Finance;
using Xunit;

namespace Networthy.IntegrationTests;

/// <summary>
/// Issue #149: the Overview hero number shipped a figure its own totals could not produce —
/// `amount: 300` beside `totalTarget: 500` / `totalSpent: 400` — because it summed per-category
/// clamped remainders while reporting unclamped totals. SPEC.md:126 sells this figure as the
/// *explainable* alternative to a competitor's black-box formula, so a number a reader cannot
/// reproduce from the payload is a defect, not a presentation choice.
///
/// Asserted as an invariant rather than a fixed expected value: the suite shares one `dev`
/// tenant, so other tests contribute budgets and spend to the same month. The invariant holds
/// regardless of what else is in there, which is exactly what makes it a durable regression.
/// </summary>
[Collection("api")]
public sealed class OverviewSafeToSpendTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task SafeToSpend_IsReproducibleFromTheTotalsShippedBesideIt_WhenABudgetIsOver()
    {
        using var client = fixture.AdminClient();

        // Dedicated categories: the starter taxonomy's names are asserted on by other tests, and
        // budgets are upserts — reusing "Groceries" here would rewrite their fixture underneath them.
        foreach (var name in new[] { "Issue149 Headroom", "Issue149 Overspent" })
        {
            (await client.PostAsJsonAsync("/api/finance/categories", new { Name = name }))
                .EnsureSuccessStatusCode();
        }

        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var services = scope.ServiceProvider;

        await services.GetRequiredService<AccountTools>()
            .CreateAccount("Issue149 Checking", "checking", "USD", 100_000);
        await services.GetRequiredService<TransactionTools>().LogOwnTransaction(
            "Issue149 Checking", 250, "ISSUE149 OVERSPEND PROBE",
            date: DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"),
            category: "Issue149 Overspent");

        var budgets = services.GetRequiredService<BudgetTools>();
        // One category deeply under (so the aggregate stays positive and the two formulas must
        // disagree), one deliberately blown (target 1 against 250 spent) — the shape that broke.
        await budgets.SetBudget("Issue149 Headroom", 50_000);
        await budgets.SetBudget("Issue149 Overspent", 1);

        var overview = await client.GetFromJsonAsync<JsonElement>("/api/finance/overview");
        var safeToSpend = overview.GetProperty("safeToSpend");

        Assert.Equal(JsonValueKind.Object, safeToSpend.ValueKind);
        var amount = safeToSpend.GetProperty("amount").GetDecimal();
        var totalTarget = safeToSpend.GetProperty("totalTarget").GetDecimal();
        var totalSpent = safeToSpend.GetProperty("totalSpent").GetDecimal();

        // The preconditions the regression needs: both budgets landed, and the overspend landed.
        // Without these the invariant below would hold trivially even against the unfixed code.
        Assert.True(totalTarget >= 50_001m, $"seeded budgets missing from the payload (totalTarget {totalTarget})");
        Assert.True(totalSpent >= 250m, $"seeded overspend missing from the payload (totalSpent {totalSpent})");

        // The defect: this read 50_000-ish (the blown category silently discarded) against totals
        // that produce 49_751-ish. A reader holding only this payload must be able to derive it.
        Assert.Equal(Math.Max(0m, totalTarget - totalSpent), amount);
    }
}

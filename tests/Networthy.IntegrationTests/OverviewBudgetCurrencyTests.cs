using System.Net.Http.Json;
using System.Text.Json;
using Networthy.Finance;
using Xunit;

namespace Networthy.IntegrationTests;

/// <summary>
/// Issue #214: the Overview tab's two spending cards contradicted each other whenever the household
/// default currency differed from the currency its budgets were created in. <c>safeToSpend</c> was
/// computed over budgets filtered down to the default currency, so a household holding one USD
/// budget and thinking in MXN got <c>safeToSpend: null</c> — which the UI renders as *"No budgets yet
/// this month"* — beside a <c>budgets</c> array listing that very budget.
///
/// The defect is the CONTRADICTION, not the arithmetic, so the regression pins the property rather
/// than a figure: <c>safeToSpend</c> may only be null when there are genuinely no budgets this
/// month. When budgets exist but cannot be combined, the payload has to say which currencies it left
/// out and why — the policy <c>NetWorthMath.Combine</c> already committed to for net worth in #173,
/// adopted here rather than re-invented. A household's own saved rate converts; a currency with no
/// saved rate is never guessed at, but it is never silent either.
///
/// Each case provisions its OWN household: sibling tests in this collection add budgets, currencies
/// and rates to <c>dev</c>, and these assertions are exact arithmetic (400 USD × 20 = 8,000 MXN),
/// not invariants a wrong conversion could still satisfy.
/// </summary>
[Collection("api")]
public sealed class OverviewBudgetCurrencyTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task Overview_NeverClaimsThereAreNoBudgets_WhileListingOne_WhenNoRateIsSaved()
    {
        using var client = await HouseholdAsync("fx214-norate");

        await CreateAccountAsync(client, "US Checking", "USD", 5_000m);
        await CreateBudgetAsync(client, "Groceries", 400m);
        await SpendAsync(client, "US Checking", "Groceries", 82.15m);

        // The household starts thinking in MXN instead. It never told us what a dollar is worth to
        // it, so the USD budget cannot be folded into an MXN figure.
        await SetDefaultCurrencyAsync(client, "MXN");

        var overview = await OverviewAsync(client);
        var safeToSpend = overview.GetProperty("safeToSpend");
        var budgets = overview.GetProperty("budgets").EnumerateArray().ToList();

        // The two halves of the contradiction, asserted in one breath: a budget IS listed…
        Assert.Single(budgets);
        Assert.Equal("Groceries", budgets[0].GetProperty("categoryName").GetString());
        // …so the spending surface may not be an unexplained null. THIS is the assertion that was
        // red before the fix: safeToSpend was `null` outright, and null is what the UI turns into
        // "No budgets yet this month".
        Assert.Equal(JsonValueKind.Object, safeToSpend.ValueKind);

        // There is still no honest combined number — inventing one by guessing a rate would be the
        // worse defect — so the figure itself stays null. What changed is that the payload now says
        // WHY, in the same shape netWorth.excluded uses.
        Assert.Equal(JsonValueKind.Null, safeToSpend.GetProperty("amount").ValueKind);
        Assert.Equal(0, safeToSpend.GetProperty("budgetCount").GetInt32());

        var excluded = Assert.Single(safeToSpend.GetProperty("excluded").EnumerateArray());
        Assert.Equal("USD", excluded.GetProperty("currencyCode").GetString());
        Assert.Equal(400m, excluded.GetProperty("target").GetDecimal());
        Assert.Equal(82.15m, excluded.GetProperty("spent").GetDecimal());
        Assert.Equal(1, excluded.GetProperty("budgetCount").GetInt32());
        Assert.Empty(safeToSpend.GetProperty("converted").EnumerateArray());
    }

    [Fact]
    public async Task Overview_ConvertsAForeignBudget_ThroughTheHouseholdsOwnSavedRate()
    {
        using var client = await HouseholdAsync("fx214-rate");

        await CreateAccountAsync(client, "US Checking", "USD", 5_000m);
        await CreateBudgetAsync(client, "Groceries", 400m);
        await SpendAsync(client, "US Checking", "Groceries", 82.15m);
        await SetDefaultCurrencyAsync(client, "MXN");
        // Now the household prices a dollar: 1 USD = 20 MXN.
        await SaveRateAsync(client, "USD", 20m);

        var safeToSpend = (await OverviewAsync(client)).GetProperty("safeToSpend");

        // 400 × 20 = 8,000 targeted; 82.15 × 20 = 1,643.00 spent; 6,357 left. Asserted as the exact
        // figures, not merely "greater than zero": converting at some other rate is still a defect.
        Assert.Equal(8_000m, safeToSpend.GetProperty("totalTarget").GetDecimal());
        Assert.Equal(1_643m, safeToSpend.GetProperty("totalSpent").GetDecimal());
        Assert.Equal(6_357m, safeToSpend.GetProperty("amount").GetDecimal());
        Assert.Equal("MXN", safeToSpend.GetProperty("currencyCode").GetString());
        Assert.Equal(1, safeToSpend.GetProperty("budgetCount").GetInt32());

        // The conversion is shown, so a reader holding only the payload can reproduce the number —
        // the #149 property, extended to cover the FX step.
        var converted = Assert.Single(safeToSpend.GetProperty("converted").EnumerateArray());
        Assert.Equal("USD", converted.GetProperty("currencyCode").GetString());
        Assert.Equal(400m, converted.GetProperty("target").GetDecimal());
        Assert.Equal(82.15m, converted.GetProperty("spent").GetDecimal());
        Assert.Equal(8_000m, converted.GetProperty("convertedTarget").GetDecimal());
        Assert.Equal(1_643m, converted.GetProperty("convertedSpent").GetDecimal());
        Assert.Equal(20m, converted.GetProperty("rateToDefault").GetDecimal());
        Assert.Empty(safeToSpend.GetProperty("excluded").EnumerateArray());
    }

    [Fact]
    public async Task Overview_IsUnchanged_ForAHouseholdThatNeverLeftItsOwnCurrency()
    {
        using var client = await HouseholdAsync("fx214-solo");

        await CreateAccountAsync(client, "US Checking", "USD", 5_000m);
        await CreateBudgetAsync(client, "Groceries", 400m);
        await SpendAsync(client, "US Checking", "Groceries", 82.15m);

        var safeToSpend = (await OverviewAsync(client)).GetProperty("safeToSpend");

        // The control case from the issue's own reproduction (step 4): both cards agreed here
        // before the fix and must still agree, unchanged, after it. An FX path that quietly
        // re-scaled the base currency would surface right here.
        Assert.Equal(317.85m, safeToSpend.GetProperty("amount").GetDecimal());
        Assert.Equal(400m, safeToSpend.GetProperty("totalTarget").GetDecimal());
        Assert.Equal(82.15m, safeToSpend.GetProperty("totalSpent").GetDecimal());
        Assert.Equal(1, safeToSpend.GetProperty("budgetCount").GetInt32());
        Assert.Empty(safeToSpend.GetProperty("converted").EnumerateArray());
        Assert.Empty(safeToSpend.GetProperty("excluded").EnumerateArray());
    }

    [Fact]
    public async Task Overview_StillReportsNoNumberAtAll_WhenTheHouseholdHasNoBudgets()
    {
        using var client = await HouseholdAsync("fx214-nobudgets");

        await CreateAccountAsync(client, "US Checking", "USD", 5_000m);

        var overview = await OverviewAsync(client);

        // The one state where "No budgets yet this month" is TRUE. #149's principle is untouched:
        // no budgets means no honest number, so null — not a fabricated zero, and not an empty
        // disclosure object either, which would make the UI hunt for a reason that does not exist.
        Assert.Empty(overview.GetProperty("budgets").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, overview.GetProperty("safeToSpend").ValueKind);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>Provisions a fresh household with the finance module and returns its admin client.</summary>
    private async Task<HttpClient> HouseholdAsync(string slug)
    {
        using var admin = fixture.AdminClient();
        using var created = await admin.PostAsJsonAsync("/api/admin/tenants/provision", new
        {
            name = $"Issue214 {slug}",
            slug,
            adminEmail = $"admin@{slug}.local",
            adminSubject = $"{slug}-admin",
            adminDisplayName = $"Issue214 {slug} Admin",
            modules = new[] { FinanceModule.Id },
        });
        created.EnsureSuccessStatusCode();

        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Subject", $"{slug}-admin");
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", slug);
        client.DefaultRequestHeaders.Add("X-Dev-Roles", "system_admin");
        return client;
    }

    private static async Task CreateAccountAsync(
        HttpClient client, string name, string currencyCode, decimal balance)
    {
        using var response = await client.PostAsJsonAsync("/api/finance/accounts", new
        {
            name, type = "checking", currencyCode, cachedBalance = balance,
        });
        response.EnsureSuccessStatusCode();
    }

    /// <summary>A budget with no explicit currency takes the household's current default — which is
    /// what makes the later default-currency change strand it, exactly as the issue reports.</summary>
    private static async Task CreateBudgetAsync(HttpClient client, string categoryName, decimal target)
    {
        using var response = await client.PostAsJsonAsync("/api/finance/budgets", new
        {
            categoryName, target,
        });
        response.EnsureSuccessStatusCode();
    }

    private static async Task SpendAsync(
        HttpClient client, string accountName, string categoryName, decimal amount)
    {
        using var response = await client.PostAsJsonAsync("/api/finance/transactions", new
        {
            accountName,
            description = $"{categoryName} run",
            amount,
            direction = "expense",
            categoryName,
        });
        response.EnsureSuccessStatusCode();
    }

    private static async Task SetDefaultCurrencyAsync(HttpClient client, string currencyCode)
    {
        using var response = await client.PostAsJsonAsync("/api/finance/settings", new
        {
            defaultCurrencyCode = currencyCode, timeZoneId = "UTC",
        });
        response.EnsureSuccessStatusCode();
    }

    private static async Task SaveRateAsync(HttpClient client, string currencyCode, decimal rateToDefault)
    {
        using var response = await client.PostAsJsonAsync("/api/finance/settings/rates", new
        {
            currencyCode, rateToDefault,
        });
        response.EnsureSuccessStatusCode();
    }

    private static async Task<JsonElement> OverviewAsync(HttpClient client) =>
        await client.GetFromJsonAsync<JsonElement>("/api/finance/overview");
}

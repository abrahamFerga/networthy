using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Networthy.Finance;
using Networthy.IntegrationTests.Evals;
using Xunit;

namespace Networthy.IntegrationTests;

/// <summary>
/// Issue #173: the Overview tab — the module's <c>home: true</c> screen, and so the first number a
/// household ever sees — summed only the accounts already denominated in the household default and
/// silently discarded every other one. A household holding 1,000 USD and 2,000 EUR with a saved
/// rate of 1.1 was told its net worth was 1,000 USD, while <c>get_net_worth</c> answered 3,200 USD
/// from the same rows. Two surfaces, one question, a 220% disagreement, and nothing on the page
/// saying anything had been left out.
///
/// The defect is the DISAGREEMENT, so the regression pins agreement rather than a single number:
/// the endpoint's figure must equal the tool's combined figure, on the same data, in the same run.
/// Both are read through their real public surfaces — HTTP for the dashboard, an AG-UI turn for the
/// assistant — which is exactly how the bug was found.
///
/// Each case provisions its OWN household instead of using the shared <c>dev</c> tenant: sibling
/// tests in this collection add accounts, currencies and rates to <c>dev</c>, and this regression
/// needs exact arithmetic (2,000 EUR × 1.1 = 2,200), not an invariant that a wrong conversion could
/// still satisfy.
/// </summary>
[Collection("api")]
public sealed class OverviewMultiCurrencyTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task Overview_CombinesForeignCurrency_ThroughSavedRates_AndAgreesWithTheTool()
    {
        using var client = await HouseholdAsync("fx173-combined");

        await CreateAccountAsync(client, "US Checking", "checking", "USD", 1_000m);
        await CreateAccountAsync(client, "EU Savings", "savings", "EUR", 2_000m);
        await SaveRateAsync(client, "EUR", 1.1m);

        var netWorth = await NetWorthAsync(client);

        // 2,000 EUR × 1.1 = 2,200, plus 1,000 USD. Asserted as the exact figure, not merely
        // "more than 1,000": a fix that converted at some other rate would still be a defect.
        Assert.Equal("USD", netWorth.GetProperty("currencyCode").GetString());
        Assert.Equal(3_200m, netWorth.GetProperty("total").GetDecimal());

        // Nothing was dropped, and the conversion is shown rather than asserted by the client.
        Assert.Empty(netWorth.GetProperty("excluded").EnumerateArray());
        var converted = Assert.Single(netWorth.GetProperty("converted").EnumerateArray());
        Assert.Equal("EUR", converted.GetProperty("currencyCode").GetString());
        Assert.Equal(2_000m, converted.GetProperty("amount").GetDecimal());
        Assert.Equal(2_200m, converted.GetProperty("convertedAmount").GetDecimal());
        Assert.Equal(1.1m, converted.GetProperty("rateToDefault").GetDecimal());

        // …and the assistant, asked the same question over AG-UI, must land on the same number.
        // This is the assertion the bug was actually about: one household, one question, one answer.
        var (combined, _) = await ToolNetWorthAsync(client);
        Assert.Equal(combined, netWorth.GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task Overview_ExcludesACurrencyWithNoSavedRate_AndSaysSo_RatherThanDroppingItSilently()
    {
        using var client = await HouseholdAsync("fx173-norate");

        await CreateAccountAsync(client, "US Checking", "checking", "USD", 1_000m);
        await CreateAccountAsync(client, "EU Savings", "savings", "EUR", 2_000m);
        await SaveRateAsync(client, "EUR", 1.1m);
        // No rate for GBP: the household never told us what a pound is worth to them.
        await CreateAccountAsync(client, "UK Current", "checking", "GBP", 500m);

        var netWorth = await NetWorthAsync(client);

        // The policy is the one NetWorthMath.Combine already committed to for the tool, adopted
        // rather than re-invented: an unrated currency is never guessed at a rate, so it stays OUT
        // of the total — but it is now REPORTED, which is the half that was missing. Silence was
        // the defect, not the exclusion.
        Assert.Equal(3_200m, netWorth.GetProperty("total").GetDecimal());
        var excluded = Assert.Single(netWorth.GetProperty("excluded").EnumerateArray());
        Assert.Equal("GBP", excluded.GetProperty("currencyCode").GetString());
        Assert.Equal(500m, excluded.GetProperty("amount").GetDecimal());

        // The tool reaches the same two answers — combined figure and the unconverted remainder.
        var (combined, replyText) = await ToolNetWorthAsync(client);
        Assert.Equal(combined, netWorth.GetProperty("total").GetDecimal());
        Assert.Contains("Not combined", replyText, StringComparison.Ordinal);
        Assert.Contains("GBP", replyText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overview_IsUnchanged_ForASingleCurrencyHousehold()
    {
        using var client = await HouseholdAsync("fx173-solo");

        await CreateAccountAsync(client, "US Checking", "checking", "USD", 1_000m);
        await CreateAccountAsync(client, "US Savings", "savings", "USD", 250m);

        var netWorth = await NetWorthAsync(client);

        // The household that never left its own currency must see exactly what it saw before —
        // a conversion path that quietly re-scaled the base currency would show up right here.
        Assert.Equal(1_250m, netWorth.GetProperty("total").GetDecimal());
        Assert.Equal("USD", netWorth.GetProperty("currencyCode").GetString());
        Assert.Empty(netWorth.GetProperty("converted").EnumerateArray());
        Assert.Empty(netWorth.GetProperty("excluded").EnumerateArray());
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>Provisions a fresh household with the finance module and returns its admin client.</summary>
    private async Task<HttpClient> HouseholdAsync(string slug)
    {
        using var admin = fixture.AdminClient();
        using var created = await admin.PostAsJsonAsync("/api/admin/tenants/provision", new
        {
            name = $"Issue173 {slug}",
            slug,
            adminEmail = $"admin@{slug}.local",
            adminSubject = $"{slug}-admin",
            adminDisplayName = $"Issue173 {slug} Admin",
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
        HttpClient client, string name, string type, string currencyCode, decimal balance)
    {
        using var response = await client.PostAsJsonAsync("/api/finance/accounts", new
        {
            name, type, currencyCode, cachedBalance = balance,
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

    private static async Task<JsonElement> NetWorthAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/finance/overview")).GetProperty("netWorth");

    /// <summary>
    /// The assistant's answer to the same question, over the real AG-UI transport. get_net_worth is
    /// a read with no required arguments, so the Mock provider reaches it on a name-token match and
    /// its own return string reaches the reply — no dependence on the Mock inventing arguments.
    /// </summary>
    private static async Task<(decimal Combined, string ReplyText)> ToolNetWorthAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/agui/finance", new
        {
            messages = new[] { new { id = Guid.NewGuid().ToString("N"), role = "user", content = "get net worth" } },
        });
        response.EnsureSuccessStatusCode();
        var run = EvalRun.Parse(await response.Content.ReadAsStringAsync());

        Assert.DoesNotContain("RUN_ERROR", run.EventTypes);
        Assert.Contains("get_net_worth", run.ToolCalls);

        // "Combined: 3,200.00 USD (your saved rates: …)" — formatted with the current culture by
        // AccountTools, so it is parsed back with the current culture rather than a guessed one.
        var match = Regex.Match(run.AssistantText, @"Combined:\s*(?<amount>\S+)\s+[A-Z]{3}");
        Assert.True(match.Success, $"get_net_worth reported no combined figure. Reply was:\n{run.AssistantText}");
        var combined = decimal.Parse(
            match.Groups["amount"].Value, NumberStyles.Number, CultureInfo.CurrentCulture);
        return (combined, run.AssistantText);
    }
}

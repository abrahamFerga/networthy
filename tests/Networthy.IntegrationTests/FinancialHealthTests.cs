using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Networthy.Finance;
using Xunit;

namespace Networthy.IntegrationTests;

/// <summary>
/// Debt tracking + the health assessment, end to end: loans carry APRs, the Debts tab prices
/// them, and get_financial_health turns the household's own numbers into standard,
/// threshold-triggered suggestions.
/// </summary>
[Collection("api")]
public sealed class FinancialHealthTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task Loans_WithApr_FlowInto_TheHealthAssessment()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var accounts = scope.ServiceProvider.GetRequiredService<AccountTools>();

        Assert.Contains("6.25% APR", await accounts.CreateAccount(
            "House Mortgage", "mortgage", "USD", 200_000, interestRateApr: 6.25, minimumMonthlyPayment: 1500));
        await accounts.CreateAccount("Health Checking", "checking", "USD", 9000);
        await accounts.CreateAccount("Store Card", "credit", "USD", -2000);

        // The unpriced card gets terms after the fact.
        Assert.Contains("23.99% APR", await accounts.UpdateAccountTerms("Store Card", 23.99));

        // The collection shares one tenant, so assert on THESE rows, not on tenant-wide totals.
        var health = await scope.ServiceProvider.GetRequiredService<HealthTools>().GetFinancialHealth();
        Assert.Contains("House Mortgage: 200,000.00 owed @ 6.25%", health);
        Assert.Contains("Store Card: 2,000.00 owed @ 23.99%", health);
        Assert.Contains("/month in interest", health);         // priced debt gets a monthly cost
        Assert.Contains("avalanche", health);                  // 23.99% card is the costliest APR
        Assert.Contains("'Store Card' at 23.99%", health);
        Assert.Contains("not financial advice", health);

        // The Debts tab prices the same rows.
        using var client = fixture.AdminClient();
        var rows = await client.GetFromJsonAsync<JsonElement>("/api/finance/debts");
        var mortgage = rows.EnumerateArray().Single(d => d.GetProperty("name").GetString() == "House Mortgage");
        Assert.Equal(200_000, mortgage.GetProperty("owed").GetDecimal());
        Assert.Equal(1041.67m, mortgage.GetProperty("monthlyInterest").GetDecimal());
        Assert.Equal(1500, mortgage.GetProperty("minimumPayment").GetDecimal());
    }

    [Fact]
    public async Task PositiveLoanBalance_IsStoredAsOwed_SoNetWorthStaysHonest()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var accounts = scope.ServiceProvider.GetRequiredService<AccountTools>();

        // "I owe 5,000" arrives positive — the module stores it negative.
        Assert.Contains("-5,000.00", await accounts.CreateAccount("Car Loan", "auto loan", "USD", 5000, interestRateApr: 7.9));
        Assert.Contains("Car Loan [loan] -5,000.00 USD @ 7.9% APR", await accounts.ListAccounts());
    }

    [Fact]
    public async Task HealthAssessment_IsReadableByHouseholdMembers()
    {
        // Health is a household question, not an admin one — the member role carries the read.
        using var member = fixture.Factory.CreateClient();
        member.DefaultRequestHeaders.Add("X-Dev-Subject", "it-member-health");
        member.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        member.DefaultRequestHeaders.Add("X-Dev-Roles", "household-member");

        var me = await member.GetFromJsonAsync<JsonElement>("/api/platform/me");
        var permissions = me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Contains("tools.finance.get_financial_health", permissions);
        Assert.DoesNotContain("tools.finance.update_account_terms", permissions);
    }
}

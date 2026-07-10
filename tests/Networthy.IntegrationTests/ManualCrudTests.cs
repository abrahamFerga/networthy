using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Networthy.IntegrationTests;

/// <summary>
/// AI-first is not chat-only: the tab editors manage the books by hand. These are direct human
/// writes (no approval gate — RBAC gates), and the ledger's invariants hold: balance edits post
/// adjustment transactions; deleting a transaction reverses its balance effect.
/// </summary>
[Collection("api")]
public sealed class ManualCrudTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task Accounts_AddEditDelete_FromTheTab_KeepTheLedgerHonest()
    {
        using var admin = fixture.AdminClient();

        // Add (a loan typed positive stores as owed).
        var create = await admin.PostAsJsonAsync("/api/finance/accounts", new
        {
            name = "Manual Mortgage", type = "mortgage", currencyCode = "usd",
            cachedBalance = 100_000, interestRateApr = 5.5, minimumMonthlyPayment = 900,
        });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var row = await FindAccountAsync(admin, "Manual Mortgage");
        Assert.Equal(-100_000, row.GetProperty("cachedBalance").GetDecimal());
        Assert.Equal(5.5m, row.GetProperty("interestRateApr").GetDecimal());

        // Edit: a changed balance becomes an adjustment transaction, not a silent rewrite.
        var edit = await admin.PostAsJsonAsync("/api/finance/accounts", new
        {
            name = "Manual Mortgage", type = "loan", currencyCode = "USD",
            cachedBalance = -99_000, interestRateApr = 5.25, minimumMonthlyPayment = 900,
        });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        row = await FindAccountAsync(admin, "Manual Mortgage");
        Assert.Equal(-99_000, row.GetProperty("cachedBalance").GetDecimal());
        Assert.Equal(5.25m, row.GetProperty("interestRateApr").GetDecimal());
        var transactions = await admin.GetFromJsonAsync<JsonElement>("/api/finance/transactions");
        Assert.Contains(transactions.EnumerateArray(),
            t => t.GetProperty("description").GetString() == "Balance adjustment (manual edit)" &&
                 t.GetProperty("accountName").GetString() == "Manual Mortgage");

        // Type/currency are fixed — the honest refusal, not a silent ignore.
        var mutate = await admin.PostAsJsonAsync("/api/finance/accounts", new
        {
            name = "Manual Mortgage", type = "checking", currencyCode = "USD", cachedBalance = -99_000,
        });
        Assert.Equal(HttpStatusCode.BadRequest, mutate.StatusCode);

        // Delete.
        var id = row.GetProperty("id").GetString();
        var delete = await admin.DeleteAsync($"/api/finance/accounts/{id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }

    [Fact]
    public async Task Transactions_AddedFromTheTab_MoveTheBalance_AndDeleteReversesIt()
    {
        using var admin = fixture.AdminClient();
        await admin.PostAsJsonAsync("/api/finance/accounts", new
        {
            name = "Manual Wallet", type = "cash", currencyCode = "USD", cachedBalance = 100,
        });

        var add = await admin.PostAsJsonAsync("/api/finance/transactions", new
        {
            accountName = "Manual Wallet", description = "Farmers market", amount = 30,
            direction = "expense", categoryName = "Groceries",
        });
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        var id = (await add.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        Assert.Equal(70, (await FindAccountAsync(admin, "Manual Wallet")).GetProperty("cachedBalance").GetDecimal());

        var delete = await admin.DeleteAsync($"/api/finance/transactions/{id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(100, (await FindAccountAsync(admin, "Manual Wallet")).GetProperty("cachedBalance").GetDecimal());
    }

    [Fact]
    public async Task Budgets_And_Goals_UpsertAndDelete_FromTheirTabs()
    {
        using var admin = fixture.AdminClient();

        var budget = await admin.PostAsJsonAsync("/api/finance/budgets", new { categoryName = "Travel", target = 300 });
        Assert.Equal(HttpStatusCode.OK, budget.StatusCode);
        var budgetRows = await admin.GetFromJsonAsync<JsonElement>("/api/finance/budgets");
        var travel = budgetRows.EnumerateArray().Single(b => b.GetProperty("categoryName").GetString() == "Travel");
        Assert.Equal(300, travel.GetProperty("target").GetDecimal());

        var goal = await admin.PostAsJsonAsync("/api/finance/goals", new
        {
            name = "Manual Emergency Fund", target = 10_000, targetDate = "2027-01-01",
        });
        Assert.Equal(HttpStatusCode.OK, goal.StatusCode);
        var goalRows = await admin.GetFromJsonAsync<JsonElement>("/api/finance/goals");
        var fund = goalRows.EnumerateArray().Single(g => g.GetProperty("name").GetString() == "Manual Emergency Fund");
        Assert.Equal("2027-01-01", fund.GetProperty("targetDate").GetString());

        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.DeleteAsync($"/api/finance/budgets/{travel.GetProperty("id").GetString()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.DeleteAsync($"/api/finance/goals/{fund.GetProperty("id").GetString()}")).StatusCode);
    }

    [Fact]
    public async Task ManualWrites_AreDeniedTo_HouseholdMembers()
    {
        using var member = fixture.Factory.CreateClient();
        member.DefaultRequestHeaders.Add("X-Dev-Subject", "it-member-crud");
        member.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        member.DefaultRequestHeaders.Add("X-Dev-Roles", "household-member");

        var create = await member.PostAsJsonAsync("/api/finance/accounts", new
        {
            name = "Sneaky Account", type = "cash", currencyCode = "USD", cachedBalance = 0,
        });
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);

        // And the payload never advertises the editor to them.
        var modules = await member.GetFromJsonAsync<JsonElement>("/api/platform/modules");
        var finance = modules.EnumerateArray().Single(m => m.GetProperty("id").GetString() == "finance");
        var accountsTab = finance.GetProperty("tabs").EnumerateArray()
            .Single(t => t.GetProperty("id").GetString() == "accounts");
        Assert.Equal(JsonValueKind.Null, accountsTab.GetProperty("editor").ValueKind);
    }

    private static async Task<JsonElement> FindAccountAsync(HttpClient client, string name)
    {
        var rows = await client.GetFromJsonAsync<JsonElement>("/api/finance/accounts");
        return rows.EnumerateArray().Single(a => a.GetProperty("name").GetString() == name);
    }
}

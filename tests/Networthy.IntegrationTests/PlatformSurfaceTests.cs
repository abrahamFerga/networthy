using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Networthy.IntegrationTests;

/// <summary>
/// The product surface a browser (or channel) actually hits: the module catalog that drives the
/// UI shell, the seeded taxonomy, RBAC enforcement on the household roles, and a real AG-UI chat
/// turn streaming through the Mock provider — the keyless posture every deployment starts in.
/// </summary>
[Collection("api")]
public sealed class PlatformSurfaceTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task Platform_ServesTheFinanceModule_WithItsTabs()
    {
        using var client = fixture.AdminClient();
        var modules = await client.GetFromJsonAsync<JsonElement>("/api/platform/modules");

        var finance = modules.EnumerateArray().Single(m => m.GetProperty("id").GetString() == "finance");
        var tabs = finance.GetProperty("tabs").EnumerateArray().Select(t => t.GetProperty("id").GetString()).ToList();
        Assert.Equal(["chat", "overview", "accounts", "transactions", "budgets", "income", "recurring", "debts", "trend", "goals", "review", "categories", "settings"], tabs);

        // The Overview tab is the declared home (epic 8): the shell opens the app on it.
        var overview = finance.GetProperty("tabs").EnumerateArray().Single(t => t.GetProperty("id").GetString() == "overview");
        Assert.True(overview.GetProperty("home").GetBoolean());
    }

    [Fact]
    public async Task Overview_ComposesTheDashboardPayload_InOneRead()
    {
        using var client = fixture.AdminClient();
        var overview = await client.GetFromJsonAsync<JsonElement>("/api/finance/overview");

        // Every section is present in the one composed payload; safeToSpend is honest —
        // null (guidance) or an object with its inputs, never a fabricated bare number.
        Assert.True(overview.TryGetProperty("currencyCode", out _));
        Assert.True(overview.TryGetProperty("netWorth", out var netWorth));
        Assert.True(netWorth.TryGetProperty("trend", out _));
        Assert.Equal(JsonValueKind.Array, overview.GetProperty("budgets").ValueKind);
        Assert.Equal(JsonValueKind.Array, overview.GetProperty("upcomingBills").ValueKind);
        Assert.Equal(JsonValueKind.Array, overview.GetProperty("recentTransactions").ValueKind);
        Assert.Equal(JsonValueKind.Array, overview.GetProperty("goals").ValueKind);
        var safeToSpend = overview.GetProperty("safeToSpend");
        if (safeToSpend.ValueKind is not JsonValueKind.Null)
        {
            Assert.True(safeToSpend.TryGetProperty("amount", out _));
            Assert.True(safeToSpend.TryGetProperty("totalTarget", out _));
            Assert.True(safeToSpend.TryGetProperty("totalSpent", out _));
        }
    }

    [Fact]
    public async Task StarterTaxonomy_SeedsTwentyCategories()
    {
        using var client = fixture.AdminClient();
        var categories = await client.GetFromJsonAsync<JsonElement>("/api/finance/categories");

        var names = categories.EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();
        Assert.True(names.Count >= 20, $"expected the 20 starter categories, got {names.Count}");
        Assert.Contains("Groceries", names);
        Assert.Contains("Salary", names);
    }

    [Fact]
    public async Task HouseholdMember_CanReadFinance_ButCannotManageCategories()
    {
        using var member = ClientFor("household-member");

        var accounts = await member.GetAsync("/api/finance/accounts");
        Assert.Equal(HttpStatusCode.OK, accounts.StatusCode);

        var upsert = await member.PostAsJsonAsync("/api/finance/categories", new { name = "Sneaky" });
        Assert.Equal(HttpStatusCode.Forbidden, upsert.StatusCode);
    }

    [Fact]
    public async Task HouseholdAdmin_CanManageCategories()
    {
        using var admin = ClientFor("household-admin");
        var upsert = await admin.PostAsJsonAsync("/api/finance/categories", new { name = "Pets" });
        Assert.Equal(HttpStatusCode.OK, upsert.StatusCode);

        var categories = await admin.GetFromJsonAsync<JsonElement>("/api/finance/categories");
        Assert.Contains("Pets", categories.EnumerateArray().Select(c => c.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task AgUiChatTurn_StreamsAFullRun_AgainstTheRealPipeline()
    {
        using var client = fixture.AdminClient();
        var response = await client.PostAsJsonAsync("/api/agui/finance", new
        {
            messages = new[] { new { id = "m1", role = "user", content = "List our accounts" } },
        });
        response.EnsureSuccessStatusCode();

        var sse = await response.Content.ReadAsStringAsync();
        Assert.Contains("RUN_STARTED", sse);
        Assert.Contains("RUN_FINISHED", sse);
        Assert.DoesNotContain("RUN_ERROR", sse);
    }

    [Fact]
    public async Task PlaidConnector_AppearsInTheAdminCatalog()
    {
        using var client = fixture.AdminClient();
        var catalog = await client.GetFromJsonAsync<JsonElement>("/api/admin/connectors");

        // Since alpha.16 the catalog is a marketplace: what this host installed vs what
        // first-party connectors exist to add. Plaid is Networthy's own, so it's installed.
        Assert.Contains("plaid",
            catalog.GetProperty("installed").EnumerateArray().Select(c => c.GetProperty("id").GetString()));
        Assert.NotEmpty(catalog.GetProperty("available").EnumerateArray());
    }

    [Fact]
    public async Task BrandedDomainUi_IsServedAtTheRoot()
    {
        // The embedded SPA (scripts/build-ui.ps1) serves from the host itself — same origin as
        // the API, no registry, branded at build time.
        using var client = fixture.Factory.CreateClient();
        var html = await client.GetStringAsync("/");
        Assert.Contains("<title>Networthy</title>", html);
    }

    private HttpClient ClientFor(string role)
    {
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Subject", $"it-{role}");
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        client.DefaultRequestHeaders.Add("X-Dev-Roles", role);
        return client;
    }
}

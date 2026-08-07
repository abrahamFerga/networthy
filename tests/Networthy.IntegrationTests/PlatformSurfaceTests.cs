using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Networthy.Finance;
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
    public async Task ForgedHostHeader_IsRejected()
    {
        using var client = fixture.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/alive");
        request.Headers.Host = "attacker.example";

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Platform_ServesTheFinanceModule_WithItsTabs()
    {
        using var client = fixture.AdminClient();
        var modules = await client.GetFromJsonAsync<JsonElement>("/api/platform/modules");

        var finance = modules.EnumerateArray().Single(m => m.GetProperty("id").GetString() == "finance");
        var tabs = finance.GetProperty("tabs").EnumerateArray().Select(t => t.GetProperty("id").GetString()).ToList();
        // Deliberate additions only: "spending" and "cashflow" joined with issue #46.
        Assert.Equal(["chat", "overview", "accounts", "transactions", "budgets", "spending", "income", "recurring", "debts", "trend", "cashflow", "goals", "review", "categories", "settings"], tabs);

        // The Overview tab is the declared home (epic 8): the shell opens the app on it.
        var overview = finance.GetProperty("tabs").EnumerateArray().Single(t => t.GetProperty("id").GetString() == "overview");
        Assert.True(overview.GetProperty("home").GetBoolean());

        // The chat starters reach the browser on this same read (issue #134) — the shell renders
        // suggestedPrompts as one-click chips in the empty chat, so a manifest that declares them
        // but a payload that drops them is still a bare pane.
        var starters = finance.GetProperty("suggestedPrompts").EnumerateArray()
            .Select(p => p.GetString()).ToList();
        Assert.Equal(FinanceCatalog.StarterPrompts.Select(p => p.Prompt), starters);
    }

    [Fact]
    public async Task Settings_IsASingletonForm_WithGuessedCurrencyAndReadableTimeZone()
    {
        using var client = fixture.AdminClient();
        var modules = await client.GetFromJsonAsync<JsonElement>("/api/platform/modules");
        var finance = modules.EnumerateArray().Single(m => m.GetProperty("id").GetString() == "finance");
        var settings = finance.GetProperty("tabs").EnumerateArray()
            .Single(t => t.GetProperty("id").GetString() == "settings");

        // One household's config, not a list — the shell renders it as a form, not a table+Add.
        Assert.True(settings.GetProperty("singleton").GetBoolean());

        var fields = settings.GetProperty("editor").GetProperty("fields").EnumerateArray().ToList();

        // Currency is a picker (free text let typos silently exclude accounts) and opens on the
        // currency the browser's locale suggests — a guess, still the user's to change.
        var currency = fields.Single(f => f.GetProperty("field").GetString() == "defaultCurrencyCode");
        var currencyOptions = OptionValues(currency);
        Assert.Contains("MXN", currencyOptions);
        Assert.Contains("USD", currencyOptions);
        Assert.Equal("browser-currency", currency.GetProperty("defaultFrom").GetString());
        Assert.Equal("Currency & time", currency.GetProperty("group").GetString());

        var timeZone = fields.Single(f => f.GetProperty("field").GetString() == "timeZoneId");
        Assert.Contains("America/Mexico_City", OptionValues(timeZone)); // stored value stays IANA
        Assert.False(timeZone.GetProperty("required").GetBoolean());    // blank = UTC
        Assert.Equal("browser-timezone", timeZone.GetProperty("defaultFrom").GetString());
        // …but the label is readable — no underscore, and the standard UTC offset appended.
        var mexicoCity = timeZone.GetProperty("options").EnumerateArray()
            .Single(o => o.GetProperty("value").GetString() == "America/Mexico_City");
        Assert.Equal("America / Mexico City (UTC-06:00)", mexicoCity.GetProperty("label").GetString());

        // The endpoint enforces the same vocabulary — a picker alone wouldn't stop the API path.
        var junk = await client.PostAsJsonAsync("/api/finance/settings", new { defaultCurrencyCode = "ZZZ" });
        Assert.Equal(HttpStatusCode.BadRequest, junk.StatusCode);
    }

    [Fact]
    public async Task SetupWizard_AsksForCurrencyOnly_TimeZoneMovedToSettings()
    {
        using var client = fixture.AdminClient();
        var modules = await client.GetFromJsonAsync<JsonElement>("/api/platform/modules");
        var finance = modules.EnumerateArray().Single(m => m.GetProperty("id").GetString() == "finance");

        var basics = finance.GetProperty("onboarding").GetProperty("steps").EnumerateArray()
            .Single(s => s.GetProperty("id").GetString() == "basics");
        var basicsFields = basics.GetProperty("fields").EnumerateArray()
            .Select(f => f.GetProperty("field").GetString()).ToList();

        // The wizard asks one question, not two: currency (guessed from the browser), no time zone —
        // it defaults from the browser in Settings, so first-run doesn't make anyone pick a zone.
        Assert.Contains("defaultCurrencyCode", basicsFields);
        Assert.DoesNotContain("timeZoneId", basicsFields);

        var currency = basics.GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("field").GetString() == "defaultCurrencyCode");
        Assert.Equal("browser-currency", currency.GetProperty("defaultFrom").GetString());
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

    /// <summary>
    /// The household admin runs the household, and both docs and the sign-in screen say so —
    /// yet the role held no <c>platform.*</c> grant at all, so every screen in the admin console
    /// answered 403. The one that mattered is AI Settings: it is the ONLY place a provider key can
    /// be entered (never deployment config, per ADR/AGENTS), so a household that self-hosts had no
    /// reachable path off the Mock provider. Integrations is the same defect one screen over — the
    /// role already grants <c>tools.connectors.plaid.*</c>, so it could call Plaid's tools while
    /// being unable to enable or configure the connector those tools need.
    /// </summary>
    [Fact]
    public async Task HouseholdAdmin_CanConfigureTheHouseholdsAiProviderAndIntegrations()
    {
        using var admin = ClientFor("household-admin");
        try
        {
            // The screen loads at all — a 403 here is what the SPA renders as
            // "Could not load AI settings", with no way forward.
            var settings = await admin.GetAsync("/api/admin/ai-settings");
            Assert.Equal(HttpStatusCode.OK, settings.StatusCode);

            // And the key actually saves. Write-only: the read-back proves storage, never the value.
            var save = await admin.PutAsJsonAsync("/api/admin/ai-settings", new
            {
                systemPrompt = (string?)null,
                maxConversationTokens = (int?)null,
                maxMonthlyTokens = (long?)null,
                provider = "OpenAI",
                model = "gpt-4.1-mini",
                endpoint = (string?)null,
                apiKey = "sk-networthy-household-admin-test",
            });
            Assert.Equal(HttpStatusCode.NoContent, save.StatusCode);

            var stored = await admin.GetFromJsonAsync<JsonElement>("/api/admin/ai-settings");
            Assert.Equal("OpenAI", stored.GetProperty("providerOverride").GetString());
            Assert.True(stored.GetProperty("hasApiKey").GetBoolean());

            // Integrations — where Plaid and the OCR engines are enabled (ADR-0007).
            var connectors = await admin.GetAsync("/api/admin/connectors");
            Assert.Equal(HttpStatusCode.OK, connectors.StatusCode);
        }
        finally
        {
            // Back to the deployment default (Mock): the rest of the suite chats through the
            // keyless posture and must never dial a real provider.
            using var cleanup = fixture.AdminClient();
            var reset = await cleanup.PutAsJsonAsync("/api/admin/ai-settings", new
            {
                systemPrompt = (string?)null,
                maxConversationTokens = (int?)null,
                maxMonthlyTokens = (long?)null,
                provider = (string?)null,
                model = (string?)null,
                endpoint = (string?)null,
                apiKey = "", // empty string = clear the vaulted key
            });
            reset.EnsureSuccessStatusCode();
        }
    }

    /// <summary>
    /// The other half of it: widening household-admin must not widen the household. A member
    /// configuring the provider key would be spending the household's budget and redirecting every
    /// conversation through an endpoint of their choosing — and tenant administration was never
    /// theirs at all.
    /// </summary>
    [Fact]
    public async Task HouseholdMember_StillCannotReachTheAdminConsole()
    {
        using var member = ClientFor("household-member");

        foreach (var endpoint in new[] { "/api/admin/ai-settings", "/api/admin/connectors" })
        {
            var response = await member.GetAsync(endpoint);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    /// <summary>
    /// Deployment-level administration stays with <c>system_admin</c>. A household admin runs one
    /// household; these reach across every household in the deployment (or edit the grants
    /// themselves, which would make the boundary above decorative).
    /// </summary>
    [Fact]
    public async Task HouseholdAdmin_CannotReachDeploymentAdministration()
    {
        using var admin = ClientFor("household-admin");

        foreach (var endpoint in new[] { "/api/admin/tenants", "/api/admin/modules" })
        {
            var response = await admin.GetAsync(endpoint);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
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
        var response = await client.GetAsync("/");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("<title>Networthy</title>", html);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'none'",
            response.Headers.GetValues("Content-Security-Policy").Single());

        using var admin = fixture.AdminClient();
        var api = await admin.GetAsync("/api/platform/modules");
        api.EnsureSuccessStatusCode();
        Assert.Contains("no-store", api.Headers.CacheControl!.ToString());
    }

    private HttpClient ClientFor(string role)
    {
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Subject", $"it-{role}");
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        client.DefaultRequestHeaders.Add("X-Dev-Roles", role);
        return client;
    }

    /// <summary>An option is {value,label} on the wire: the value posts, the label is read.</summary>
    private static List<string?> OptionValues(JsonElement field) =>
        field.GetProperty("options").EnumerateArray()
            .Select(o => o.GetProperty("value").GetString())
            .ToList();
}

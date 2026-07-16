using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Networthy.Finance;
using Xunit;

namespace Networthy.IntegrationTests;

/// <summary>
/// The household's preferences shape every number: the default currency flows into tools that
/// were "USD" literals, and the time zone defines "today". Note: the collection shares one
/// tenant, so the timezone test computes its expectation from the SAME setting it wrote.
/// </summary>
[Collection("api")]
public sealed class HouseholdSettingsTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task DefaultCurrency_FlowsThrough_ToolsAndForms()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var services = scope.ServiceProvider;

        var saved = await services.GetRequiredService<HouseholdSettingsTools>()
            .UpdateHouseholdSettings(defaultCurrency: "mxn");
        Assert.Contains("currency MXN", saved);

        // No currency spoken anywhere — the household default applies.
        var created = await services.GetRequiredService<AccountTools>()
            .CreateAccount("Cuenta Citibanamex", "checking", currency: null, openingBalance: 110_000);
        Assert.Contains("(MXN)", created);

        var budget = await services.GetRequiredService<BudgetTools>().SetBudget("Groceries", 4000);
        Assert.Contains("MXN", budget);

        // Restore for the rest of the shared collection.
        await services.GetRequiredService<HouseholdSettingsTools>().UpdateHouseholdSettings(defaultCurrency: "USD");
    }

    /// <summary>
    /// The "Forms" half of the test above, which only ever exercised the tools. The tab editors and
    /// the setup wizard post over HTTP with the currency left blank, and each of these endpoints
    /// substituted a literal "USD" — handing an MXN household USD budgets, goals and income while
    /// the chat tools resolved the default correctly.
    /// </summary>
    [Fact]
    public async Task DefaultCurrency_FlowsThrough_TheFormsHttpEndpoints()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var tools = scope.ServiceProvider.GetRequiredService<HouseholdSettingsTools>();
        await tools.UpdateHouseholdSettings(defaultCurrency: "mxn");

        using var admin = fixture.AdminClient();
        try
        {
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/finance/budgets",
                new { categoryName = "Groceries", target = 5_000 })).StatusCode);
            Assert.Equal("MXN", await CurrencyOfAsync(admin, "/api/finance/budgets", "categoryName", "Groceries"));

            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/finance/goals",
                new { name = "Fondo de emergencia", target = 60_000 })).StatusCode);
            Assert.Equal("MXN", await CurrencyOfAsync(admin, "/api/finance/goals", "name", "Fondo de emergencia"));

            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/finance/income-sources",
                new { name = "Sueldo", amount = 40_000, cadence = "monthly" })).StatusCode);
            Assert.Equal("MXN", await CurrencyOfAsync(admin, "/api/finance/income-sources", "name", "Sueldo"));
        }
        finally
        {
            await tools.UpdateHouseholdSettings(defaultCurrency: "USD"); // restore the shared tenant
        }
    }

    private static async Task<string?> CurrencyOfAsync(
        HttpClient client, string url, string keyField, string key)
    {
        var rows = await client.GetFromJsonAsync<JsonElement>(url);
        foreach (var row in rows.EnumerateArray())
        {
            if (string.Equals(row.GetProperty(keyField).GetString(), key, StringComparison.OrdinalIgnoreCase))
            {
                return row.GetProperty("currencyCode").GetString();
            }
        }

        return null;
    }

    [Fact]
    public async Task TimeZone_Defines_TheHouseholdsToday()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var services = scope.ServiceProvider;
        var tools = services.GetRequiredService<HouseholdSettingsTools>();

        // Pick whichever extreme zone is currently on a DIFFERENT date than UTC — ASK, don't guess
        // from the clock. This used to read `utcNow.Hour >= 12 ? Kiritimati : Pago_Pago`, which is
        // wrong for exactly one hour a day: at 11:xx UTC, Pago_Pago (UTC-11) has just rolled onto
        // the SAME date, so the assertion below failed every day between 11:00 and 11:59 UTC and
        // reddened whatever PR happened to run then. One of the two extremes always differs — at
        // 11:xx UTC that's Kiritimati (UTC+14, already tomorrow) — so let the zones answer.
        var utcNow = DateTime.UtcNow;
        var utcToday = DateOnly.FromDateTime(utcNow);
        var zone = Networthy.Finance.Persistence.HouseholdSettings.TodayIn("Pacific/Pago_Pago") != utcToday
            ? "Pacific/Pago_Pago"   // UTC-11: still yesterday
            : "Pacific/Kiritimati"; // UTC+14: already tomorrow
        var saved = await tools.UpdateHouseholdSettings(timeZone: zone);
        Assert.Contains(zone, saved);

        var expected = Networthy.Finance.Persistence.HouseholdSettings.TodayIn(zone);
        Assert.NotEqual(DateOnly.FromDateTime(utcNow), expected); // the whole point

        // A transaction logged "today" lands on the household's date, not UTC's.
        await services.GetRequiredService<AccountTools>().CreateAccount("TZ Wallet", "cash", "USD", 10);
        var logged = await services.GetRequiredService<TransactionTools>()
            .LogOwnTransaction("TZ Wallet", 1, "tz probe");
        Assert.Contains(expected.ToString("yyyy-MM-dd"), logged);

        await tools.UpdateHouseholdSettings(timeZone: "UTC"); // restore
    }

    [Fact]
    public async Task ExchangeRates_CombineMultiCurrencyNetWorth_OnlyThroughSavedRates()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var services = scope.ServiceProvider;
        var settingsTools = services.GetRequiredService<HouseholdSettingsTools>();
        var accounts = services.GetRequiredService<AccountTools>();

        await settingsTools.UpdateHouseholdSettings(defaultCurrency: "MXN");
        await accounts.CreateAccount("FX Pesos", "checking", "MXN", 50_000);
        await accounts.CreateAccount("FX Dollars", "savings", "USD", 1_000);

        // Before a rate exists the USD total is reported apart — never silently guessed.
        var before = await accounts.GetNetWorth();
        Assert.Contains("Not combined", before);
        Assert.Contains("set_exchange_rate", before);

        var saved = await settingsTools.SetExchangeRate("usd", 17m);
        Assert.Contains("1 USD = 17 MXN", saved);

        var after = await accounts.GetNetWorth();
        Assert.Contains("Combined:", after);

        // The default currency itself is refused — its rate is 1 by definition.
        Assert.Contains("default currency", await settingsTools.SetExchangeRate("MXN", 20m));

        await settingsTools.UpdateHouseholdSettings(defaultCurrency: "USD"); // restore for the collection
    }

    [Fact]
    public async Task HealthThresholds_ComeFromSettings()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var services = scope.ServiceProvider;
        var settingsTools = services.GetRequiredService<HouseholdSettingsTools>();

        var accounts = services.GetRequiredService<AccountTools>();
        await accounts.CreateAccount("Tuning Card", "credit", "USD", -1_000);
        await accounts.UpdateAccountTerms("Tuning Card", interestRateApr: 20);

        // Default threshold (8%): a 20% card triggers the avalanche suggestion.
        var flagged = await services.GetRequiredService<HealthTools>().GetFinancialHealth("USD");
        Assert.Contains("avalanche", flagged);

        // Raise the household's bar above the card's APR — the flag disappears.
        await settingsTools.UpdateHouseholdSettings(highAprThresholdPercent: 30);
        var calm = await services.GetRequiredService<HealthTools>().GetFinancialHealth("USD");
        Assert.DoesNotContain("avalanche", calm);

        await settingsTools.UpdateHouseholdSettings(highAprThresholdPercent: 8); // restore
    }

    [Fact]
    public async Task SettingsTab_ReadsAndWrites_TheSingleton()
    {
        using var admin = fixture.AdminClient();

        var update = await admin.PostAsJsonAsync("/api/finance/settings", new
        {
            defaultCurrencyCode = "USD", timeZoneId = "UTC", billReminderLeadDays = 5,
        });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var rows = await admin.GetFromJsonAsync<JsonElement>("/api/finance/settings");
        var row = rows.EnumerateArray().Single();
        Assert.Equal(5, row.GetProperty("billReminderLeadDays").GetInt32());
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", row.GetProperty("todayThere").GetString());

        // Garbage time zones are refused, not stored.
        var bad = await admin.PostAsJsonAsync("/api/finance/settings", new
        {
            defaultCurrencyCode = "USD", timeZoneId = "Mars/Olympus_Mons",
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // Members read, never write.
        using var member = fixture.Factory.CreateClient();
        member.DefaultRequestHeaders.Add("X-Dev-Subject", "it-member-settings");
        member.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        member.DefaultRequestHeaders.Add("X-Dev-Roles", "household-member");
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/api/finance/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await member.PostAsJsonAsync("/api/finance/settings", new { defaultCurrencyCode = "EUR" })).StatusCode);

        // Restore the shared tenant's lead days.
        await admin.PostAsJsonAsync("/api/finance/settings", new
        {
            defaultCurrencyCode = "USD", timeZoneId = "UTC", billReminderLeadDays = 3,
        });
    }
}

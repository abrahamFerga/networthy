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

    [Fact]
    public async Task TimeZone_Defines_TheHouseholdsToday()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var services = scope.ServiceProvider;
        var tools = services.GetRequiredService<HouseholdSettingsTools>();

        // Pick whichever extreme zone is currently on a DIFFERENT date than UTC.
        var utcNow = DateTime.UtcNow;
        var zone = utcNow.Hour >= 12 ? "Pacific/Kiritimati" : "Pacific/Pago_Pago"; // UTC+14 / UTC-11
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

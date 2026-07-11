using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Networthy.IntegrationTests;

/// <summary>
/// The admin console's finance extension: exchange rates get a curated surface of their own.
/// The page is declared in the manifest (permission-gated — a member never learns it exists),
/// rendered generically, and its endpoints share the chat tool's validation, so a rate is the
/// same object whether it arrived from a form or from an approved AI call.
/// </summary>
[Collection("api")]
public sealed class AdminExtensionTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task ExchangeRatesPage_IsDeclared_ForAdmins_AndInvisibleToMembers()
    {
        using var admin = fixture.AdminClient();
        var extensions = await admin.GetFromJsonAsync<JsonElement>("/api/admin/extensions");

        var finance = extensions.EnumerateArray()
            .Single(e => e.GetProperty("id").GetString() == "finance");
        var tab = Assert.Single(finance.GetProperty("tabs").EnumerateArray());
        Assert.Equal("exchange-rates", tab.GetProperty("id").GetString());
        Assert.Equal("/api/finance/settings/rates", tab.GetProperty("dataEndpoint").GetString());
        // The editor ships to the admin — add/edit/delete straight from the generic page.
        Assert.Equal("currencyCode", tab.GetProperty("editor").GetProperty("keyField").GetString());

        // A household member (no finance.manage) is not told the page exists at all.
        using var member = MemberClient("it-ext-member");
        var forMember = await member.GetFromJsonAsync<JsonElement>("/api/admin/extensions");
        Assert.DoesNotContain(
            forMember.EnumerateArray(),
            e => e.GetProperty("id").GetString() == "finance");
    }

    [Fact]
    public async Task ExchangeRates_CrudRoundTrip_WithToolGradeValidation()
    {
        using var admin = fixture.AdminClient();

        // Upsert goes through the same validation as the chat tool: garbage codes are refused…
        var bad = await admin.PostAsJsonAsync("/api/finance/settings/rates", new
        {
            currencyCode = "notacurrency", rateToDefault = 5m,
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // …the default currency is refused (its rate is 1 by definition; shared tenant is USD)…
        var self = await admin.PostAsJsonAsync("/api/finance/settings/rates", new
        {
            currencyCode = "USD", rateToDefault = 2m,
        });
        Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);

        // …and a real one lands, listed with its human meaning.
        var save = await admin.PostAsJsonAsync("/api/finance/settings/rates", new
        {
            currencyCode = "jpy", rateToDefault = 0.0065m,
        });
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        var rates = await admin.GetFromJsonAsync<JsonElement>("/api/finance/settings/rates");
        var jpy = rates.EnumerateArray().Single(r => r.GetProperty("currencyCode").GetString() == "JPY");
        Assert.Contains("1 JPY = 0.0065 USD", jpy.GetProperty("meaning").GetString());

        // Members read, never write.
        using var member = MemberClient("it-ext-rates-member");
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/api/finance/settings/rates")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await member.PostAsJsonAsync("/api/finance/settings/rates", new { currencyCode = "EUR", rateToDefault = 1.1m })).StatusCode);

        // Delete restores the shared tenant — and that currency honestly returns to "not combined".
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/finance/settings/rates/jpy")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync("/api/finance/settings/rates/jpy")).StatusCode);
    }

    private HttpClient MemberClient(string subject)
    {
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        client.DefaultRequestHeaders.Add("X-Dev-Roles", "household-member");
        return client;
    }
}

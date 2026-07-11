using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Networthy.IntegrationTests;

/// <summary>
/// Bill reminders are the household's only notification stream, and since alpha.16 each member
/// can mute them for themselves: the finance module declares the "finance.bill" category, the
/// platform exposes it on the preferences switchboard, and a mute is personal — stored per user,
/// never tenant-wide.
/// </summary>
[Collection("api")]
public sealed class NotificationPreferenceTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task BillReminders_AreAPerUserMutableCategory()
    {
        using var member = ClientFor("it-notify-prefs");

        // The declared category surfaces on the switchboard, on by default.
        var preferences = await member.GetFromJsonAsync<JsonElement>("/api/notifications/preferences");
        var bill = preferences.EnumerateArray()
            .Single(p => p.GetProperty("id").GetString() == "finance.bill");
        Assert.Equal("finance", bill.GetProperty("moduleId").GetString());
        Assert.Equal("Bill reminders", bill.GetProperty("label").GetString());
        Assert.True(bill.GetProperty("enabled").GetBoolean());

        // This member mutes it; the switchboard reflects their stance.
        var mute = await member.PutAsJsonAsync("/api/notifications/preferences/finance.bill", new { enabled = false });
        Assert.Equal(HttpStatusCode.OK, mute.StatusCode);
        var after = await member.GetFromJsonAsync<JsonElement>("/api/notifications/preferences");
        Assert.False(after.EnumerateArray()
            .Single(p => p.GetProperty("id").GetString() == "finance.bill")
            .GetProperty("enabled").GetBoolean());

        // Another member's stance is untouched — the mute is personal.
        using var other = ClientFor("it-notify-prefs-other");
        var others = await other.GetFromJsonAsync<JsonElement>("/api/notifications/preferences");
        Assert.True(others.EnumerateArray()
            .Single(p => p.GetProperty("id").GetString() == "finance.bill")
            .GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task UndeclaredCategories_AreRefused_NotStored()
    {
        using var member = ClientFor("it-notify-prefs-bogus");
        var response = await member.PutAsJsonAsync("/api/notifications/preferences/finance.made-up", new { enabled = false });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private HttpClient ClientFor(string subject)
    {
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        client.DefaultRequestHeaders.Add("X-Dev-Roles", "household-member");
        return client;
    }
}

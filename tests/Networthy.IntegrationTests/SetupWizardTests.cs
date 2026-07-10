using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Networthy.IntegrationTests;

/// <summary>
/// The first-run setup wizard's server half: the manifest declares it (permission-gated),
/// and the upload step's follow-up endpoint feeds the real import pipeline.
/// </summary>
[Collection("api")]
public sealed class SetupWizardTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task Manifest_DeclaresTheWizard_ForAdmins_AndHidesIt_FromMembers()
    {
        using var admin = fixture.AdminClient();
        var modules = await admin.GetFromJsonAsync<JsonElement>("/api/platform/modules");
        var finance = modules.EnumerateArray().Single(m => m.GetProperty("id").GetString() == "finance");

        var onboarding = finance.GetProperty("onboarding");
        Assert.Equal("/api/finance/accounts", onboarding.GetProperty("probeEndpoint").GetString());
        var steps = onboarding.GetProperty("steps").EnumerateArray()
            .Select(s => s.GetProperty("id").GetString()).ToList();
        Assert.Equal(["welcome", "basics", "accounts", "income", "statements", "loans", "budget", "done"], steps);

        // The income step presets the direction so the generic transactions endpoint serves it.
        var income = onboarding.GetProperty("steps").EnumerateArray()
            .Single(s => s.GetProperty("id").GetString() == "income");
        Assert.Equal("income", income.GetProperty("preset").GetProperty("direction").GetString());

        using var member = fixture.Factory.CreateClient();
        member.DefaultRequestHeaders.Add("X-Dev-Subject", "it-member-wizard");
        member.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        member.DefaultRequestHeaders.Add("X-Dev-Roles", "household-member");
        var memberModules = await member.GetFromJsonAsync<JsonElement>("/api/platform/modules");
        var memberFinance = memberModules.EnumerateArray().Single(m => m.GetProperty("id").GetString() == "finance");
        Assert.Equal(JsonValueKind.Null, memberFinance.GetProperty("onboarding").ValueKind);
    }

    [Fact]
    public async Task WizardUpload_FeedsTheRealImportPipeline()
    {
        using var admin = fixture.AdminClient();
        await admin.PostAsJsonAsync("/api/finance/accounts", new
        {
            name = "Wizard Checking", type = "checking", currencyCode = "USD", cachedBalance = 500,
        });

        // The wizard's upload step: file to the platform store, then the follow-up POST.
        using var form = new MultipartFormDataContent();
        var csv = new ByteArrayContent(Encoding.UTF8.GetBytes("Date,Description,Amount\n2026-07-01,WIZARD COFFEE,-4.50\n"));
        csv.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");
        form.Add(csv, "file", "wizard-statement.csv");
        var upload = await admin.PostAsync("/api/files/", form);
        Assert.True(upload.IsSuccessStatusCode, $"file upload returned {(int)upload.StatusCode}");
        var fileId = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var queue = await admin.PostAsJsonAsync("/api/finance/imports", new
        {
            fileId, accountName = "Wizard Checking",
        });
        Assert.Equal(HttpStatusCode.OK, queue.StatusCode);
        var message = (await queue.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString();
        Assert.Contains("queued for extraction", message);
    }
}

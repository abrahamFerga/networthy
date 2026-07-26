using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Networthy.Finance;
using Plenipo.Application.Documents;
using Xunit;

namespace Networthy.IntegrationTests;

/// <summary>
/// Scanned-statement OCR as connectors: engines are enabled and configured per household under
/// Integrations (platform-vaulted secrets), the read-only Document scanning page reports which
/// engine wins, and the scoped OcrEngineRouter owns the platform's IOcrEngine slot so a change
/// applies on the next upload — no restart, and a dead source degrades instead of crashing.
/// </summary>
[Collection("api")]
public sealed class OcrSettingsTests(IntegrationFixture fixture)
{
    private const string TikaUrl = "http://tika:9998";
    private const string AdiEndpoint = "https://it-household.cognitiveservices.azure.com";

    [Fact]
    public async Task OcrStatus_FollowsConnectorPriority_TikaOverAdiOverOff()
    {
        using var admin = fixture.AdminClient();
        try
        {
            // Fresh household, no connectors, no deployment Ocr config: honestly off.
            var initial = await GetStatus(admin);
            Assert.Equal("None", initial.GetProperty("engine").GetString());

            // Configure + enable both engines; the self-hosted one outranks the managed cloud —
            // if you bothered to run your own OCR server, your documents stay home.
            await PutSettings(admin, OcrEngineRouter.AdiConnectorId, new Dictionary<string, string>
            {
                [OcrEngineRouter.AdiEndpointKey] = AdiEndpoint,
                [OcrEngineRouter.AdiApiKeyKey] = "di-key-integration-test",
            });
            await Enable(admin, OcrEngineRouter.AdiConnectorId);
            await PutSettings(admin, OcrEngineRouter.TikaConnectorId, new Dictionary<string, string>
            {
                [OcrEngineRouter.TikaBaseUrlKey] = TikaUrl,
            });
            await Enable(admin, OcrEngineRouter.TikaConnectorId);

            var both = await GetStatus(admin);
            Assert.Equal("Self-hosted (Apache Tika)", both.GetProperty("engine").GetString());
            Assert.Equal("Integrations connector", both.GetProperty("source").GetString());
            Assert.Equal(TikaUrl, both.GetProperty("detail").GetString());

            // Disable Tika → the ADI connector takes over.
            await Disable(admin, OcrEngineRouter.TikaConnectorId);
            var adiOnly = await GetStatus(admin);
            Assert.Equal("Azure Document Intelligence", adiOnly.GetProperty("engine").GetString());
            Assert.Equal(AdiEndpoint, adiOnly.GetProperty("detail").GetString());

            // Disable ADI too → off again (this deployment has no env-config fallback).
            await Disable(admin, OcrEngineRouter.AdiConnectorId);
            Assert.Equal("None", (await GetStatus(admin)).GetProperty("engine").GetString());
        }
        finally
        {
            using var cleanup = fixture.AdminClient();
            await cleanup.PostAsync($"/api/admin/connectors/{OcrEngineRouter.TikaConnectorId}/disable", null);
            await cleanup.PostAsync($"/api/admin/connectors/{OcrEngineRouter.AdiConnectorId}/disable", null);
        }
    }

    [Fact]
    public async Task OcrConnectors_AreListed_WithVaultedWriteOnlySecrets()
    {
        using var admin = fixture.AdminClient();

        var catalog = await admin.GetFromJsonAsync<JsonElement>("/api/admin/connectors");
        var installed = catalog.GetProperty("installed").EnumerateArray().ToList();

        var tika = installed.Single(c => c.GetProperty("id").GetString() == OcrEngineRouter.TikaConnectorId);
        Assert.Contains(
            tika.GetProperty("settings").EnumerateArray(),
            s => s.GetProperty("key").GetString() == OcrEngineRouter.TikaAuthorizationKey
                 && s.GetProperty("isSecret").GetBoolean());

        var adi = installed.Single(c => c.GetProperty("id").GetString() == OcrEngineRouter.AdiConnectorId);
        var keySetting = adi.GetProperty("settings").EnumerateArray()
            .Single(s => s.GetProperty("key").GetString() == OcrEngineRouter.AdiApiKeyKey);
        Assert.True(keySetting.GetProperty("isSecret").GetBoolean());

        try
        {
            // Save a secret, then re-read the catalog: the platform reports THAT a value exists,
            // never the value — and the raw key appears nowhere in the payload.
            await PutSettings(admin, OcrEngineRouter.AdiConnectorId, new Dictionary<string, string>
            {
                [OcrEngineRouter.AdiEndpointKey] = AdiEndpoint,
                [OcrEngineRouter.AdiApiKeyKey] = "di-secret-never-echoed",
            });
            var after = await admin.GetFromJsonAsync<JsonElement>("/api/admin/connectors");
            var adiAfter = after.GetProperty("installed").EnumerateArray()
                .Single(c => c.GetProperty("id").GetString() == OcrEngineRouter.AdiConnectorId);
            Assert.True(adiAfter.GetProperty("settings").EnumerateArray()
                .Single(s => s.GetProperty("key").GetString() == OcrEngineRouter.AdiApiKeyKey)
                .GetProperty("hasValue").GetBoolean());
            Assert.DoesNotContain("di-secret-never-echoed", after.GetRawText());
        }
        finally
        {
            // Blank the stored values so later tests meet a clean tenant.
            using var cleanup = fixture.AdminClient();
            await PutSettings(cleanup, OcrEngineRouter.AdiConnectorId, new Dictionary<string, string>
            {
                [OcrEngineRouter.AdiEndpointKey] = "",
                [OcrEngineRouter.AdiApiKeyKey] = "",
            });
        }
    }

    [Fact]
    public async Task Router_OwnsThePlatformOcrSlot_AndDegradesToNullWhenSourcesFail()
    {
        using var admin = fixture.AdminClient();
        try
        {
            // Point the enabled Tika connector at a port nothing listens on (the Development
            // posture allows loopback destinations, so the egress policy lets it through).
            await PutSettings(admin, OcrEngineRouter.TikaConnectorId, new Dictionary<string, string>
            {
                [OcrEngineRouter.TikaBaseUrlKey] = "http://127.0.0.1:9",
            });
            await Enable(admin, OcrEngineRouter.TikaConnectorId);

            var (scope, _, _) = await fixture.AuthorizedScopeAsync();
            using (scope)
            {
                var engine = scope.ServiceProvider.GetRequiredService<IOcrEngine>();
                Assert.IsType<OcrEngineRouter>(engine);

                // Unreachable server + no further sources → an honest null, not an exception:
                // a dead OCR box must fail the page read, never the whole import pipeline.
                using var content = new MemoryStream([1, 2, 3]);
                Assert.Null(await engine.ExtractTextAsync(content, "application/pdf"));
            }
        }
        finally
        {
            using var cleanup = fixture.AdminClient();
            await cleanup.PostAsync($"/api/admin/connectors/{OcrEngineRouter.TikaConnectorId}/disable", null);
        }
    }

    [Fact]
    public async Task OcrStatus_IsAdminOnly()
    {
        using var member = MemberClient("it-ocr-member");
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/finance/settings/ocr")).StatusCode);
    }

    private static async Task<JsonElement> GetStatus(HttpClient client)
    {
        var rows = await client.GetFromJsonAsync<JsonElement>("/api/finance/settings/ocr");
        return Assert.Single(rows.EnumerateArray());
    }

    private static async Task Enable(HttpClient client, string connectorId)
        => (await client.PostAsync($"/api/admin/connectors/{connectorId}/enable", null)).EnsureSuccessStatusCode();

    private static async Task Disable(HttpClient client, string connectorId)
        => (await client.PostAsync($"/api/admin/connectors/{connectorId}/disable", null)).EnsureSuccessStatusCode();

    private static async Task PutSettings(HttpClient client, string connectorId, Dictionary<string, string> values)
        => (await client.PutAsJsonAsync($"/api/admin/connectors/{connectorId}/settings", new { values }))
            .EnsureSuccessStatusCode();

    private HttpClient MemberClient(string subject)
    {
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        client.DefaultRequestHeaders.Add("X-Dev-Roles", "household-member");
        return client;
    }
}

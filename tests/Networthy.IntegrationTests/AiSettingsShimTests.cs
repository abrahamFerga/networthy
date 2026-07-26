using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Networthy.IntegrationTests;

/// <summary>
/// The host's shims over the frozen alpha.25 AI seams (PlenipoAiShims): saving the admin form
/// with the "Default" model no longer fails for OpenAI, and the demo-mode flag the shell's
/// banner reads follows the tenant's effective provider instead of the deployment default.
/// </summary>
[Collection("api")]
public sealed class AiSettingsShimTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task DefaultModelSave_Succeeds_AndDemoFlag_FollowsTenantProvider()
    {
        using var admin = fixture.AdminClient();
        try
        {
            // Baseline: the Development deployment default is Mock, so the shell is in demo mode.
            var info = await admin.GetFromJsonAsync<JsonElement>("/api/platform/info");
            Assert.True(info.GetProperty("demoMode").GetBoolean());

            // The admin SPA's "Default: gpt-4o-mini" option submits model=null. The platform
            // validator alone answers 400 "model is required for the OpenAI provider" and the
            // whole save — key included — is lost; the shim resolves the default instead.
            var save = await admin.PutAsJsonAsync("/api/admin/ai-settings", new
            {
                systemPrompt = (string?)null,
                maxConversationTokens = (int?)null,
                maxMonthlyTokens = (long?)null,
                provider = "OpenAI",
                model = (string?)null,
                endpoint = (string?)null,
                apiKey = "sk-networthy-integration-test",
            });
            Assert.Equal(HttpStatusCode.NoContent, save.StatusCode);

            // The row stores the deployment default explicitly — what "Default" means.
            var settings = await admin.GetFromJsonAsync<JsonElement>("/api/admin/ai-settings");
            Assert.Equal("OpenAI", settings.GetProperty("providerOverride").GetString());
            Assert.Equal("gpt-4o-mini", settings.GetProperty("modelOverride").GetString());
            Assert.True(settings.GetProperty("hasApiKey").GetBoolean());

            // And the banner flag now tells the truth: a configured tenant is not a demo.
            info = await admin.GetFromJsonAsync<JsonElement>("/api/platform/info");
            Assert.False(info.GetProperty("demoMode").GetBoolean());
            Assert.True(info.GetProperty("chatEnabled").GetBoolean());
        }
        finally
        {
            // Return the tenant to the deployment default (Mock) — the rest of the suite chats
            // through the keyless posture and must not accidentally dial OpenAI.
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

    [Fact]
    public async Task DemoFlag_ReturnsToTrue_WhenTenantOverrideIsCleared()
    {
        using var admin = fixture.AdminClient();
        var info = await admin.GetFromJsonAsync<JsonElement>("/api/platform/info");

        // With no tenant override row (or after clearing one), the deployment answer stands.
        Assert.True(info.GetProperty("demoMode").GetBoolean());
        Assert.True(info.GetProperty("chatEnabled").GetBoolean());
    }

    [Fact]
    public async Task ModelCatalog_ForNonOpenAiProviders_PassesThroughUntouched()
    {
        using var admin = fixture.AdminClient();

        // Mock has no catalog; the shim must relay the platform's message verbatim rather than
        // filtering (the same passthrough protects an Ollama install's local model names).
        var response = await admin.PostAsJsonAsync("/api/admin/ai-models", new
        {
            provider = "Mock",
            endpoint = (string?)null,
            apiKey = (string?)null,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(body.GetProperty("models").EnumerateArray());
        Assert.False(body.GetProperty("supportsDiscovery").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));
    }
}

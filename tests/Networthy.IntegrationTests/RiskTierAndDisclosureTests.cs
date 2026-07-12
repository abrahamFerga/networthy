using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cortex.Modules.Sdk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Networthy.Finance;
using Networthy.Finance.Persistence;
using Xunit;

namespace Networthy.IntegrationTests;

/// <summary>
/// Issue #49's promises, pinned against the real host: the ADMT disclosure read is reachable
/// by a plain household member, the finance tools' risk tiers are a deliberate exact list,
/// and a chat-tool transaction is tagged as the assistant's while a form one stays the human's.
/// </summary>
[Collection("api")]
public sealed class RiskTierAndDisclosureTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task HouseholdMember_CanReadTheAiDecisionDisclosure()
    {
        // Disclosure is a member read, not an admin one (contrast /api/admin/audit/*): an
        // ADMT ask comes from any member of the household. The platform ships the surface;
        // this pins that THIS host actually exposes it.
        using var member = ClientFor("household-member");
        var response = await member.GetAsync("/api/platform/ai-decisions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entries = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, entries.ValueKind);
    }

    [Fact]
    public async Task FinanceTools_LowRiskTier_IsPinnedExactly()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var tools = scope.ServiceProvider.GetServices<IModuleToolSource>()
            .Single(s => s.ModuleId == FinanceModule.Id)
            .GetTools(scope.ServiceProvider);

        // Deliberate exact list: High is the default and nothing downgrades silently, so a
        // future tool joins the compact one-tap tier only by editing BOTH the declaration
        // (rationale block in FinanceToolSource) and this guard.
        Assert.Equal(
            ["categorize_transaction", "contribute_to_goal", "set_exchange_rate"],
            tools.Where(t => t.Risk == ApprovalRisk.Low).Select(t => t.Name).Order());

        // Risk is review-surface ceremony for GATED tools; Low on an ungated tool would be
        // dead configuration masking a missing gate.
        Assert.All(tools.Where(t => t.Risk == ApprovalRisk.Low), t => Assert.True(t.RequiresApproval));
    }

    [Fact]
    public async Task TransactionOrigin_ChatToolWritesAssistant_FormWritesManual()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var services = scope.ServiceProvider;
        await services.GetRequiredService<AccountTools>().CreateAccount("Origin Checking", "checking", "USD", 100);

        // The chat-tool path — even the ungated quick capture — is the model choosing the
        // arguments, so the row must carry the AI-origin tag.
        await services.GetRequiredService<TransactionTools>()
            .LogOwnTransaction("Origin Checking", 12.34, "Origin chat coffee");

        // The form path is the human acting directly; its vocabulary is unchanged.
        using var admin = fixture.AdminClient();
        var posted = await admin.PostAsJsonAsync("/api/finance/transactions", new
        {
            accountName = "Origin Checking", description = "Origin form snack",
            amount = 5.67, direction = "expense",
        });
        posted.EnsureSuccessStatusCode();

        var db = services.GetRequiredService<FinanceDbContext>();
        Assert.Equal("assistant",
            (await db.Transactions.SingleAsync(t => t.Description == "Origin chat coffee")).Source);
        Assert.Equal("manual",
            (await db.Transactions.SingleAsync(t => t.Description == "Origin form snack")).Source);

        // …and the origin is visible where the household reads its ledger, not just stored.
        var rows = await admin.GetFromJsonAsync<JsonElement>("/api/finance/transactions");
        var chat = rows.EnumerateArray().Single(r => r.GetProperty("description").GetString() == "Origin chat coffee");
        Assert.Equal("assistant", chat.GetProperty("source").GetString());
        var form = rows.EnumerateArray().Single(r => r.GetProperty("description").GetString() == "Origin form snack");
        Assert.Equal("manual", form.GetProperty("source").GetString());
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

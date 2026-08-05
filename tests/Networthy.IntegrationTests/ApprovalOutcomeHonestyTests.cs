using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Networthy.Finance;
using Networthy.Finance.Persistence;
using Plenipo.Application.Approvals;
using Plenipo.Core.Platform;
using Xunit;

namespace Networthy.IntegrationTests;

/// <summary>
/// Issue #182: an approved write that did NOT take effect must not be reported as a successful
/// execution.
///
/// <para>
/// The platform classifies an approved re-execution by exception — <c>ApprovalExecutor</c> treats
/// any non-throwing return as success, and <c>ApprovalStatus.Failed</c> is documented as "approved,
/// but the tool threw when it ran". Networthy's write tools used to report domain refusals by
/// RETURNING a sentence, so a refusal was indistinguishable from a success: the approval landed
/// <c>Executed</c> with <c>Error = null</c>, the transcript said "'set_budget' ran", and the ADMT
/// disclosure surface repeated it — while nothing had been written.
/// </para>
///
/// <para>
/// Both halves are asserted here, because a fix that only made refusals fail could be satisfied by
/// calling everything a failure. The refusal round trip must report failure AND leave the domain
/// untouched; the successful round trip must still report success.
/// </para>
///
/// <para>
/// Everything that decides the outcome runs over HTTP through <see
/// cref="IntegrationFixture.AdminClient"/> — the real approve endpoint, executor, announcer and
/// disclosure read. <c>AuthorizedScopeAsync()</c> appears only to PARK the pending action with
/// chosen arguments and to seed a category; it bypasses RBAC and the approval gate, so it could
/// never prove any of what is asserted below. Parking directly (rather than driving the Mock
/// provider through chat) is deliberate: the Mock fills an unfillable required string with the
/// literal "example", so the arguments under test have to be chosen, not hoped for.
/// </para>
/// </summary>
[Collection("api")]
public sealed class ApprovalOutcomeHonestyTests(IntegrationFixture fixture)
{
    /// <summary>
    /// The reported defect, end to end: approving a <c>set_budget</c> for a category that does not
    /// exist must be recorded and disclosed as a failure carrying the reason — not as a clean
    /// execution — and must leave the Budgets surface exactly as it was.
    /// </summary>
    [Fact]
    public async Task ApprovedWrite_ThatTheDomainRefuses_IsReportedAsFailed_AndWritesNothing()
    {
        var missingCategory = $"no-such-category-{Guid.NewGuid():N}";
        var approvalId = await ParkAsync("set_budget", $$"""
            {"category":"{{missingCategory}}","amount":400}
            """);

        using var admin = fixture.AdminClient();
        using var approve = await admin.PostAsync($"/api/chat/approvals/{approvalId}/approve", content: null);

        // 1. The click is answered honestly. 422, not 200 Executed.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, approve.StatusCode);

        // 2. The tool's own guidance survives — the point of the fix is that refusing does not cost
        //    the user the explanation. A stack trace or a framework message here would be a
        //    different defect, not a fix.
        var problem = await approve.Content.ReadFromJsonAsync<JsonElement>();
        var detail = problem.GetProperty("detail").GetString();
        Assert.Equal($"No category named '{missingCategory}' exists. Check the Categories tab first.", detail);

        // 3. Nothing was written. The dev tenant is shared by the whole suite, so this asserts on
        //    THIS category rather than on an empty list.
        Assert.DoesNotContain(await BudgetCategoryNamesAsync(admin), n => n == missingCategory);

        // 4. The regulatory disclosure surface no longer asserts a clean approved action. It still
        //    reports the human decision (a failed execution IS an approved decision), but now
        //    carries the error.
        var decision = await DecisionAsync(admin, approvalId);
        Assert.Equal("approval", decision.GetProperty("source").GetString());
        Assert.Equal("approved", decision.GetProperty("oversight").GetString());
        Assert.Equal(detail, decision.GetProperty("error").GetString());
    }

    /// <summary>
    /// The other half, and the guard against "fix" by relabelling: a gated write whose
    /// preconditions hold still executes, still reports success, and still discloses with no error.
    /// </summary>
    [Fact]
    public async Task ApprovedWrite_ThatSucceeds_StillReportsExecuted_WithNoError()
    {
        var category = $"honesty-{Guid.NewGuid():N}";
        await SeedCategoryAsync(category);
        var approvalId = await ParkAsync("set_budget", $$"""
            {"category":"{{category}}","amount":250}
            """);

        using var admin = fixture.AdminClient();
        using var approve = await admin.PostAsync($"/api/chat/approvals/{approvalId}/approve", content: null);

        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        var outcome = await approve.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Executed", outcome.GetProperty("status").GetString());
        Assert.Contains(category, outcome.GetProperty("result").GetString()!, StringComparison.Ordinal);

        // The write really landed, and the disclosure records an approved action with no error.
        Assert.Contains(await BudgetCategoryNamesAsync(admin), n => n == category);
        var decision = await DecisionAsync(admin, approvalId);
        Assert.Equal("approved", decision.GetProperty("oversight").GetString());
        Assert.True(
            decision.GetProperty("error").ValueKind == JsonValueKind.Null,
            "a successful approved write disclosed an error");
    }

    /// <summary>
    /// Parks a pending approval with exactly these arguments, as the middleware would have, and
    /// returns its id.
    /// </summary>
    private async Task<Guid> ParkAsync(string toolName, string argumentsJson)
    {
        var (scope, tenantId, userId) = await fixture.AuthorizedScopeAsync();
        using var _ = scope;
        var store = scope.ServiceProvider.GetRequiredService<IApprovalStore>();
        var pending = new PendingApproval
        {
            TenantId = tenantId,
            UserId = userId,
            UserDisplay = "Dev User",
            ConversationId = Guid.CreateVersion7(),
            ModuleId = FinanceModule.Id,
            ToolName = toolName,
            ArgumentsJson = argumentsJson,
        };
        await store.RecordPendingAsync(pending);
        return pending.Id;
    }

    private async Task SeedCategoryAsync(string name)
    {
        var (scope, tenantId, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var db = scope.ServiceProvider.GetRequiredService<FinanceDbContext>();
        db.Categories.Add(new Category { TenantId = tenantId, Name = name });
        await db.SaveChangesAsync();
    }

    private static async Task<List<string>> BudgetCategoryNamesAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/finance/budgets");
        response.EnsureSuccessStatusCode();
        var budgets = await response.Content.ReadFromJsonAsync<JsonElement>();
        return [.. budgets.EnumerateArray().Select(b => b.GetProperty("categoryName").GetString() ?? "")];
    }

    /// <summary>
    /// The one disclosure row for this approval. Asserting there is exactly ONE also guards the
    /// double-count trap: a gated call's audit shadow carries the blocked marker and is excluded,
    /// so a resolved approval must never appear twice (once as its own decision, once as an
    /// "automatic" tool call).
    /// </summary>
    private static async Task<JsonElement> DecisionAsync(HttpClient client, Guid approvalId)
    {
        using var response = await client.GetAsync("/api/platform/ai-decisions?take=500");
        response.EnsureSuccessStatusCode();
        var entries = await response.Content.ReadFromJsonAsync<JsonElement>();
        var matches = entries.EnumerateArray()
            .Where(e => e.GetProperty("id").GetGuid() == approvalId)
            .ToList();

        Assert.Single(matches);
        return matches[0];
    }
}

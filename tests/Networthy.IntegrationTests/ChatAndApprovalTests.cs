using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Networthy.IntegrationTests;

/// <summary>
/// The human-in-the-loop gate, end to end over HTTP — the platform invariant Networthy's whole
/// safety story rests on: an AI-suggested write never becomes financial fact until a human says so.
///
/// This suite exists because asserting <c>RequiresApproval = true</c> on a descriptor (as
/// FinanceCatalogTests does) is a STATIC check: it proves the flag is set, not that the gate fires.
/// Without these tests the gate could break and CI would stay green — the worst failure available
/// on this platform.
///
/// Everything here goes through <see cref="IntegrationFixture.AdminClient"/> and the real pipeline.
/// <c>AuthorizedScopeAsync()</c> deliberately bypasses RBAC and approvals, so it can never prove any
/// of this.
///
/// **What the Mock provider can and cannot prove.** The Mock selects a tool by name-token match and
/// fills a required string it cannot infer with <c>"example"</c> — so `create_account` is reached
/// with <c>type: "example"</c>, which the tool correctly rejects. That means "a row appeared" is not
/// an available assertion here. What IS the platform's contract, and what these tests assert, is
/// that the call is *intercepted before execution*, *re-executed on approval*, and *never executed
/// on rejection* — proven from the append-only audit log, which records every invocation.
/// </summary>
[Collection("api")]
public sealed class ChatAndApprovalTests(IntegrationFixture fixture)
{
    private const string GatedTool = "create_account";

    /// <summary>The audit error the platform records when it parks a side-effecting call.</summary>
    private const string BlockedError = "Blocked: tool requires human approval";

    /// <summary>Block → pending → approve → the exact recorded call is re-executed.</summary>
    [Fact]
    public async Task ApprovalGatedWrite_IsNotExecuted_UntilApproved()
    {
        using var client = fixture.AdminClient();
        var executedBefore = await ExecutedCountAsync(client, GatedTool);
        var blockedBefore = await BlockedCountAsync(client, GatedTool);

        var sse = await ChatAsync(client, "Create a checking account called Gate Approve");

        // The gate fired: intercepted, not executed, and the model did not narrate a success it
        // never performed.
        Assert.Contains("approval_required", sse, StringComparison.Ordinal);
        Assert.DoesNotContain("RUN_ERROR", sse, StringComparison.Ordinal);

        // The interception is on the record, and nothing ran. These two together are what fail if
        // the gate stops intercepting: the call would execute and never be recorded as blocked.
        Assert.Equal(blockedBefore + 1, await BlockedCountAsync(client, GatedTool));
        Assert.Equal(executedBefore, await ExecutedCountAsync(client, GatedTool));

        var pending = await FindPendingAsync(client, GatedTool, "Gate Approve");
        var id = pending.GetProperty("id").GetString()!;

        // The recorded call carries the arguments the model chose — that is what gets re-run.
        Assert.Contains("Gate Approve", pending.GetProperty("argumentsJson").GetString()!, StringComparison.Ordinal);

        using var approve = await client.PostAsync($"/api/chat/approvals/{id}/approve", content: null);
        approve.EnsureSuccessStatusCode();
        var outcome = await approve.Content.ReadFromJsonAsync<JsonElement>();

        // The executor ran the recorded call: 'result' is the tool's OWN return value, which only
        // the tool body can produce. (The re-execution is recorded on the approval record, not as a
        // second tool-call audit entry — ApprovalExecutor does not write to the audit log.)
        Assert.Equal("Executed", outcome.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(outcome.GetProperty("result").GetString()));
        Assert.DoesNotContain(await PendingAsync(client), p => IdOf(p) == id);
    }

    /// <summary>Reject discards the call: it never executes, and it leaves the queue.</summary>
    [Fact]
    public async Task ApprovalGatedWrite_NeverExecutes_WhenRejected()
    {
        using var client = fixture.AdminClient();
        var before = await ExecutedCountAsync(client, GatedTool);

        Assert.Contains(
            "approval_required",
            await ChatAsync(client, "Create a savings account called Gate Reject"),
            StringComparison.Ordinal);

        var id = (await FindPendingAsync(client, GatedTool, "Gate Reject")).GetProperty("id").GetString()!;

        using var reject = await client.PostAsync($"/api/chat/approvals/{id}/reject", content: null);
        reject.EnsureSuccessStatusCode();
        var outcome = await reject.Content.ReadFromJsonAsync<JsonElement>();

        // Rejected, and the executor was never invoked — so there is no tool result to report.
        Assert.Equal("Rejected", outcome.GetProperty("status").GetString());
        Assert.True(
            !outcome.TryGetProperty("result", out var r) ||
            r.ValueKind == JsonValueKind.Null ||
            string.IsNullOrEmpty(r.GetString()),
            "a rejected call still produced a tool result");
        Assert.Equal(before, await ExecutedCountAsync(client, GatedTool));
        Assert.DoesNotContain(await PendingAsync(client), p => IdOf(p) == id);
    }

    /// <summary>
    /// Resolving a pending action needs <c>chat.approvals.manage</c>. A household member can be
    /// gated but may not clear the gate — otherwise the approval lane is decoration.
    /// </summary>
    [Fact]
    public async Task HouseholdMember_CannotResolveApprovals()
    {
        using var admin = fixture.AdminClient();
        var before = await ExecutedCountAsync(admin, GatedTool);

        Assert.Contains(
            "approval_required",
            await ChatAsync(admin, "Create a cash account called Gate Rbac"),
            StringComparison.Ordinal);
        var id = (await FindPendingAsync(admin, GatedTool, "Gate Rbac")).GetProperty("id").GetString()!;

        using var member = ClientFor("household-member");
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/chat/approvals")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await member.PostAsync($"/api/chat/approvals/{id}/approve", content: null)).StatusCode);

        // The failed attempt executed nothing and left the item pending.
        Assert.Equal(before, await ExecutedCountAsync(admin, GatedTool));

        using var cleanup = await admin.PostAsync($"/api/chat/approvals/{id}/reject", content: null);
        cleanup.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// ADR-0005's one ungated write: the caller's own quick capture posts immediately. Guards the
    /// gate against over-reach — a gate on everything is as wrong as a gate on nothing.
    /// </summary>
    [Fact]
    public async Task OwnTransactionCapture_IsNotGated()
    {
        using var client = fixture.AdminClient();

        var sse = await ChatAsync(client, "Log own transaction 4.25 coffee");

        Assert.Contains("TOOL_CALL_START", sse, StringComparison.Ordinal);
        Assert.DoesNotContain("approval_required", sse, StringComparison.Ordinal);
        Assert.DoesNotContain("RUN_ERROR", sse, StringComparison.Ordinal);

        // It ran for real, and the audit log says so.
        Assert.Contains(await AuditAsync(client), e =>
            e.GetProperty("toolName").GetString() == "log_own_transaction" &&
            e.GetProperty("success").GetBoolean());
    }

    /// <summary>
    /// Both probes are mapped. <c>/health</c> is asserted deliberately: health endpoints have been
    /// Development-only by accident before, which 404s production liveness probes against a
    /// perfectly healthy app.
    /// </summary>
    [Fact]
    public async Task LivenessAndHealth_AreBothMapped()
    {
        using var client = fixture.AdminClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/alive")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static async Task<string> ChatAsync(HttpClient client, string content)
    {
        using var response = await client.PostAsJsonAsync("/api/agui/finance", new
        {
            messages = new[] { new { id = Guid.NewGuid().ToString("N"), role = "user", content } },
        });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<JsonElement[]> PendingAsync(HttpClient client) =>
        [.. (await client.GetFromJsonAsync<JsonElement>("/api/chat/approvals")).EnumerateArray()];

    private static async Task<JsonElement[]> AuditAsync(HttpClient client) =>
        [.. (await client.GetFromJsonAsync<JsonElement>("/api/admin/audit/tool-calls")).EnumerateArray()];

    /// <summary>
    /// SUCCESSFUL invocations of one tool in the append-only log — the evidence it actually ran.
    /// A blocked call is audited too, carrying <see cref="BlockedError"/>, so a plain count would
    /// not distinguish "intercepted" from "executed" — which is the whole thing under test.
    /// </summary>
    private static async Task<int> ExecutedCountAsync(HttpClient client, string toolName) =>
        (await AuditAsync(client)).Count(e =>
            e.GetProperty("toolName").GetString() == toolName &&
            e.GetProperty("success").GetBoolean() &&
            e.GetProperty("error").ValueKind == JsonValueKind.Null);

    /// <summary>Blocked-pending-approval entries for one tool.</summary>
    private static async Task<int> BlockedCountAsync(HttpClient client, string toolName) =>
        (await AuditAsync(client)).Count(e =>
            e.GetProperty("toolName").GetString() == toolName &&
            (e.GetProperty("error").GetString() ?? string.Empty).Contains(BlockedError, StringComparison.Ordinal));

    private static string? IdOf(JsonElement approval) => approval.GetProperty("id").GetString();

    /// <summary>
    /// Finds THIS test's pending call. Matching on the marker inside the recorded arguments keeps
    /// the suite parallel-safe — several tests block the same tool at once.
    /// </summary>
    private static async Task<JsonElement> FindPendingAsync(HttpClient client, string toolName, string marker)
    {
        var match = (await PendingAsync(client)).FirstOrDefault(p =>
            p.GetProperty("toolName").GetString() == toolName &&
            (p.TryGetProperty("argumentsJson", out var a) ? a.GetString() ?? string.Empty : string.Empty)
                .Contains(marker, StringComparison.OrdinalIgnoreCase));

        Assert.True(
            match.ValueKind != JsonValueKind.Undefined,
            $"no pending '{toolName}' approval carrying '{marker}' — the call was not parked for review");
        return match;
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

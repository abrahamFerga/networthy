using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Networthy.IntegrationTests;

/// <summary>
/// Networthy #150. A pending approval belongs to the conversation that proposed it: that binding
/// between the request and the consent is what makes approval-first writes mean anything. Ask for
/// one conversation's queue and you must get that conversation's queue — not the household's.
///
/// The defect this pins was observed in the UI (a brand-new, empty chat rendering another thread's
/// card with live Approve/Reject buttons), but the UI is only the messenger: the server handed the
/// full set to a caller that asked for one conversation, so <em>no</em> client could have scoped it
/// correctly. That is why the regression is pinned HERE, at the HTTP contract, rather than in a
/// component test — a UI-only fix would leave the endpoint still leaking cross-conversation rows to
/// every other caller.
///
/// Everything goes through <see cref="IntegrationFixture.AdminClient"/> and the real pipeline.
/// <c>AuthorizedScopeAsync()</c> bypasses RBAC and approvals outright, so it could never prove this.
///
/// Parallel-safe by construction: <c>dev</c> is a shared fixture tenant and sibling tests park their
/// own approvals concurrently, so every assertion is keyed to ids this test created. It never
/// asserts a total count.
/// </summary>
[Collection("api")]
public sealed class ApprovalConversationScopeTests(IntegrationFixture fixture)
{
    private const string GatedTool = "create_account";

    /// <summary>
    /// Two conversations, one parked write each. Asking for A's queue must return A's row and must
    /// not return B's.
    /// </summary>
    [Fact]
    public async Task PendingApprovals_AreScopedToTheRequestedConversation()
    {
        using var client = fixture.AdminClient();

        // Distinct thread ids ⇒ distinct conversations: AguiEndpoints derives the conversation id
        // from (tenantId, threadId), so two threads can never collapse into one conversation.
        var nonce = Guid.NewGuid().ToString("N")[..8];
        var markerA = $"ScopeA{nonce}";
        var markerB = $"ScopeB{nonce}";

        await ChatAsync(client, $"thread-a-{nonce}", $"Create a checking account called {markerA}");
        await ChatAsync(client, $"thread-b-{nonce}", $"Create a checking account called {markerB}");

        var all = await PendingAsync(client, conversationId: null);
        var a = FindByMarker(all, markerA);
        var b = FindByMarker(all, markerB);

        var conversationA = a.GetProperty("conversationId").GetString()!;
        var conversationB = b.GetProperty("conversationId").GetString()!;
        var idA = a.GetProperty("id").GetString()!;
        var idB = b.GetProperty("id").GetString()!;

        // Precondition — if these ever collapse, the rest of the test proves nothing.
        Assert.NotEqual(conversationA, conversationB);

        try
        {
            var scoped = await PendingAsync(client, conversationA);

            // The row that belongs here is still here: the filter narrows, it does not empty.
            Assert.Contains(scoped, p => p.GetProperty("id").GetString() == idA);

            // …and the row from the other thread is gone. This is the defect: before the fix the
            // endpoint ignored the filter and returned the household's whole queue, so a caller
            // scoped to A — including a brand-new, empty chat — was handed B's pending write with
            // live Approve/Reject controls.
            Assert.DoesNotContain(scoped, p => p.GetProperty("id").GetString() == idB);

            // Nothing at all from another conversation leaks through. Stronger than the two
            // assertions above and still parallel-safe: sibling tests each use their own thread.
            Assert.All(scoped, p => Assert.Equal(conversationA, p.GetProperty("conversationId").GetString()));
        }
        finally
        {
            // Leave the shared tenant's queue as it was found.
            await RejectAsync(client, idA);
            await RejectAsync(client, idB);
        }
    }

    /// <summary>
    /// The unscoped call is the household-wide review queue and must keep working exactly as before —
    /// the fix narrows only when the caller asks it to. Without this, "scope it" could be
    /// mis-implemented as "return nothing unless a conversation is named", which would silently
    /// strand every parked write.
    /// </summary>
    [Fact]
    public async Task PendingApprovals_WithoutAConversation_StillReturnTheWholeQueue()
    {
        using var client = fixture.AdminClient();

        var nonce = Guid.NewGuid().ToString("N")[..8];
        var marker = $"ScopeAll{nonce}";
        await ChatAsync(client, $"thread-all-{nonce}", $"Create a checking account called {marker}");

        var all = await PendingAsync(client, conversationId: null);
        var id = FindByMarker(all, marker).GetProperty("id").GetString()!;

        try
        {
            Assert.Contains(all, p => p.GetProperty("id").GetString() == id);
        }
        finally
        {
            await RejectAsync(client, id);
        }
    }

    /// <summary>
    /// A conversation with nothing parked in it returns an empty queue rather than the household's.
    /// This is the reported symptom stated as a contract: the brand-new, empty chat.
    /// </summary>
    [Fact]
    public async Task PendingApprovals_ForAConversationWithNothingParked_AreEmpty()
    {
        using var client = fixture.AdminClient();

        var nonce = Guid.NewGuid().ToString("N")[..8];
        var marker = $"ScopeElsewhere{nonce}";
        await ChatAsync(client, $"thread-elsewhere-{nonce}", $"Create a checking account called {marker}");

        var all = await PendingAsync(client, conversationId: null);
        var parked = FindByMarker(all, marker);
        var id = parked.GetProperty("id").GetString()!;

        try
        {
            // A conversation id that exists as far as the caller is concerned but has parked
            // nothing — the empty chat still showing its placeholder.
            var scoped = await PendingAsync(client, Guid.NewGuid().ToString());

            Assert.DoesNotContain(scoped, p => p.GetProperty("id").GetString() == id);
            Assert.Empty(scoped);
        }
        finally
        {
            await RejectAsync(client, id);
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>Sends one turn on a named thread, so the caller controls which conversation it lands in.</summary>
    private static async Task ChatAsync(HttpClient client, string threadId, string content)
    {
        using var response = await client.PostAsJsonAsync("/api/agui/finance", new
        {
            threadId,
            messages = new[] { new { id = Guid.NewGuid().ToString("N"), role = "user", content } },
        });
        response.EnsureSuccessStatusCode();

        // The gate has to have fired, or there is no approval to scope and the test would pass vacuously.
        var sse = await response.Content.ReadAsStringAsync();
        Assert.Contains("approval_required", sse, StringComparison.Ordinal);
    }

    private static async Task<JsonElement[]> PendingAsync(HttpClient client, string? conversationId)
    {
        var url = conversationId is null
            ? "/api/chat/approvals"
            : $"/api/chat/approvals?conversationId={Uri.EscapeDataString(conversationId)}";
        using var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return [.. (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()];
    }

    private static async Task RejectAsync(HttpClient client, string id)
    {
        using var _ = await client.PostAsync($"/api/chat/approvals/{id}/reject", content: null);
    }

    /// <summary>
    /// Finds this test's own parked call. Matching the marker inside the recorded arguments is what
    /// keeps the suite parallel-safe — several tests park <c>create_account</c> at the same time.
    /// </summary>
    private static JsonElement FindByMarker(JsonElement[] approvals, string marker)
    {
        var match = approvals.FirstOrDefault(p =>
            p.GetProperty("toolName").GetString() == GatedTool &&
            (p.TryGetProperty("argumentsJson", out var a) ? a.GetString() ?? string.Empty : string.Empty)
                .Contains(marker, StringComparison.OrdinalIgnoreCase));

        Assert.True(
            match.ValueKind != JsonValueKind.Undefined,
            $"no pending '{GatedTool}' approval carrying '{marker}' — the call was not parked for review");
        return match;
    }
}

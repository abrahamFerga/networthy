using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Networthy.Host;

/// <summary>
/// TODO(plenipo#111): delete this file, and its call in Program.cs, once Plenipo's
/// <c>ApprovalEndpoints</c> scopes its pending list by conversation itself.
///
/// Networthy #150. A pending approval belongs to the conversation that proposed it — that binding
/// between the request and the consent is what makes approval-first writes mean anything. In
/// Plenipo 0.1.0-alpha.28 the list endpoint does not honour it:
///
/// <code>
/// group.MapGet("/", async (IApprovalStore store, …) => { var pending = await store.ListPendingAsync(ct); … })
/// </code>
///
/// There is no <c>conversationId</c> parameter to bind, so a minimal-API route simply ignores the
/// one a caller sends and answers with the household's ENTIRE queue. Every row already carries its
/// own <c>conversationId</c> in the response DTO, so the data needed to scope the answer is present
/// and unused. Observed: <c>GET /api/chat/approvals?conversationId=A</c> returning B's parked write.
///
/// The visible symptom is in the shell — a brand-new, empty chat still showing its
/// "Start a conversation…" placeholder renders another thread's card with live Approve/Reject
/// buttons — but the shell is only the messenger. <c>PendingApprovals</c> in <c>@plenipo/ui</c>
/// queries <c>["approvals"]</c> unscoped and filters on <c>moduleId</c> alone; <c>ChatPanel</c>
/// holds the conversation id and never passes it down. Both halves are platform-owned, so the
/// client half cannot be fixed from this repo (ADR-0008: Networthy owns the Overview dashboard,
/// not the chat surface) and is filed upstream with this one. The server half is the load-bearing
/// one regardless: while the endpoint hands the full set to any caller, no client could scope it
/// correctly, and a UI-only fix would still leak cross-conversation rows to every other caller.
///
/// This shim NARROWS and never grants, which is the only direction a shim over an authorization
/// surface may move:
/// <list type="bullet">
///   <item>it runs strictly outside the platform's own pipeline, so
///     <c>RequireAuthorization(chat.approvals.manage)</c> still decides who may read the queue at
///     all — a 403 is relayed byte-for-byte and never parsed;</item>
///   <item>tenant isolation is untouched: <c>ListPendingAsync</c> is already tenant-scoped, and this
///     only removes rows from what it returned;</item>
///   <item>no <c>conversationId</c> means no scoping requested, so the household-wide review queue —
///     the admin surface, and every existing caller — is answered exactly as before.</item>
/// </list>
///
/// Deliberately narrow and deletion-ready: one path, one query parameter, no reusable abstraction.
/// </summary>
public static class ApprovalScopeShim
{
    /// <summary>The platform's pending-approvals list. <c>MapGroup</c> + <c>MapGet("/")</c> answers both forms.</summary>
    private const string ApprovalsPath = "/api/chat/approvals";

    private const string ConversationIdParameter = "conversationId";

    /// <summary>
    /// Must be registered BEFORE <c>RunPlenipoPlatformAsync()</c> (which calls
    /// <c>UsePlenipoPlatform()</c>): the platform's endpoint routing is what eventually produces the
    /// body this filters, so the shim has to be the outer of the two.
    /// </summary>
    public static WebApplication UseApprovalScopeShim(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (!HttpMethods.IsGet(context.Request.Method) || !IsApprovalsList(context.Request.Path))
            {
                await next();
                return;
            }

            // Absent, or present-but-empty: the caller asked for the household-wide queue. Relay the
            // platform's answer untouched — narrowing it here would strand every parked write in a
            // surface that legitimately reviews all of them.
            var requested = context.Request.Query[ConversationIdParameter].ToString();
            if (string.IsNullOrWhiteSpace(requested))
            {
                await next();
                return;
            }

            await FilterToConversationAsync(context, next, requested);
        });

        return app;
    }

    /// <summary>Matches <c>/api/chat/approvals</c> and its trailing-slash form, but never <c>/{id}/approve</c>.</summary>
    private static bool IsApprovalsList(PathString path) =>
        path.Equals(ApprovalsPath, StringComparison.OrdinalIgnoreCase) ||
        path.Equals(ApprovalsPath + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Buffers the platform's response and drops every row belonging to another conversation.
    /// Anything that is not a 200 JSON array — a 403 from the permission policy, a problem
    /// document, an unparseable body — passes through byte-for-byte, so the platform's own status
    /// codes and messages survive intact.
    /// </summary>
    private static async Task FilterToConversationAsync(HttpContext context, Func<Task> next, string requested)
    {
        var original = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next();

            if (context.Response.StatusCode == StatusCodes.Status200OK
                && context.Response.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true)
            {
                buffer.Position = 0;
                JsonNode? node = null;
                try
                {
                    node = await JsonNode.ParseAsync(buffer, cancellationToken: context.RequestAborted);
                }
                catch (JsonException)
                {
                    // Unparseable 200 — fall through and relay it unchanged.
                }

                if (node is JsonArray approvals)
                {
                    var scoped = new JsonArray();
                    foreach (var approval in approvals.ToArray())
                    {
                        if (!BelongsTo(approval, requested))
                        {
                            continue;
                        }
                        // Detach before re-parenting: a node may not belong to two documents.
                        approvals.Remove(approval);
                        scoped.Add(approval);
                    }

                    var bytes = Encoding.UTF8.GetBytes(scoped.ToJsonString());
                    context.Response.ContentLength = bytes.Length;
                    await original.WriteAsync(bytes, context.RequestAborted);
                    return;
                }
            }

            buffer.Position = 0;
            await buffer.CopyToAsync(original, context.RequestAborted);
        }
        finally
        {
            context.Response.Body = original;
        }
    }

    /// <summary>
    /// Whether one pending record belongs to the requested conversation. Compared as
    /// <see cref="Guid"/>s so formatting can never decide an authorization-shaped question; a value
    /// that is not a well-formed id, or a row without one, matches NOTHING. That is the fail-closed
    /// direction: a malformed request gets an empty queue, never the household's.
    /// </summary>
    private static bool BelongsTo(JsonNode? approval, string requested) =>
        approval is JsonObject row
        && row.TryGetPropertyValue("conversationId", out var value)
        && Guid.TryParse(value?.GetValue<string>(), out var rowConversation)
        && Guid.TryParse(requested, out var wanted)
        && rowConversation == wanted;
}

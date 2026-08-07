using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Networthy.Finance;
using Plenipo.Application.Agents;
using Xunit;

namespace Networthy.IntegrationTests;

/// <summary>
/// Issue #172: <c>/hubs/agent</c> — the SignalR chat transport — resolved the caller from HTTP
/// headers only. A browser's WebSocket upgrade cannot set custom headers, so <c>@microsoft/signalr</c>
/// moves the dev-auth identity into the query string; the platform's dev-auth handler never looked
/// there and fell back to its default principal (subject <c>dev-user</c>, tenant <c>dev</c>, role
/// <c>system_admin</c> ⇒ permissions <c>["*"]</c>).
///
/// Two invariants broke at once, which is why this is p0: the turn ran in the WRONG TENANT, and it
/// ran with permissions the caller does not hold — an RBAC check that should have filtered a tool
/// out before the model request was even built.
///
/// **These are matched-transport tests, deliberately.** Every run below uses the same hub, the same
/// tenant, the same role and the same message; the only variable is WHERE the identity is carried.
/// A unit test of a helper could not have caught this, because nothing about the helper was wrong —
/// the transport was. So the control leg carries identity in headers (which always worked) and the
/// subject leg carries it in the query string (which did not), and the assertion is that the two
/// agree. That also makes the negative assertions non-vacuous: <see cref="RbacIsAppliedFromTheQueryString"/>
/// proves, in the same run, that this message DOES reach <c>set_goal</c> for a role that holds it.
/// </summary>
[Collection("api")]
public sealed class HubIdentityTests(IntegrationFixture fixture)
{
    /// <summary>Its own tenant: the bug's whole signature is a turn landing in <c>dev</c> instead.</summary>
    private const string Tenant = "hub-identity";

    private const string Member = "hub-member";
    private const string Admin = "hub-admin";

    /// <summary>
    /// The dev-auth default principal the buggy path fell back to (DevAuthenticationHandler:
    /// subject "dev-user", tenant "dev", roles absent ⇒ system_admin).
    /// </summary>
    private const string FallbackSubject = "dev-user";
    private const string FallbackTenant = "dev";

    /// <summary>A write <c>household-admin</c> holds and <c>household-member</c> does not.</summary>
    private const string AdminOnlyTool = "set_goal";

    private sealed record ConversationRow(Guid Id);

    /// <summary>
    /// The tenant axis. A hub turn whose identity exists only in the query string must run as that
    /// caller in that household — not as the dev-auth fallback.
    ///
    /// RED before the fix: the conversation the turn creates belongs to <c>dev-user</c> in <c>dev</c>,
    /// so <see cref="Member"/> in <see cref="Tenant"/> has none and the fallback identity gains one.
    /// </summary>
    [Fact]
    public async Task QueryStringIdentity_RunsAsTheCaller_NotTheDevAuthFallback()
    {
        await EnsureTenantAsync();

        var callerBefore = await ConversationCountAsync(Tenant, Member, "household-member");
        var fallbackBefore = await ConversationCountAsync(FallbackTenant, FallbackSubject, "system_admin");

        await StreamOverHubAsync(
            identityInQuery: Identity(Member, "household-member"),
            identityInHeaders: null,
            message: "how are the goals doing");

        // The turn belongs to the caller who opened the socket...
        Assert.Equal(callerBefore + 1, await ConversationCountAsync(Tenant, Member, "household-member"));

        // ...and crossed no tenant boundary on the way. Asserted as a delta because `dev` is the
        // shared fixture tenant and sibling tests in this collection chat there too.
        Assert.Equal(fallbackBefore, await ConversationCountAsync(FallbackTenant, FallbackSubject, "system_admin"));
    }

    /// <summary>
    /// The RBAC axis, as a matched triple over ONE message ("set goal …"):
    ///
    /// <list type="number">
    /// <item>hub + headers as <c>household-admin</c> — holds <c>set_goal</c>, so the gate parks it.
    /// This is the control that keeps the two negatives below meaningful: it proves the message
    /// still routes to <c>set_goal</c> when the caller may call it.</item>
    /// <item>hub + headers as <c>household-member</c> — does not hold it, so it is filtered out
    /// before the model request is built. This transport was always correct.</item>
    /// <item>hub + QUERY STRING as <c>household-member</c> — the reported defect. Must match (2).</item>
    /// </list>
    ///
    /// RED before the fix: (3) runs as <c>system_admin</c> and parks a <c>set_goal</c> approval.
    /// </summary>
    [Fact]
    public async Task RbacIsAppliedFromTheQueryString()
    {
        await EnsureTenantAsync();
        const string message = "set goal 4242";

        var privileged = await StreamOverHubAsync(
            identityInQuery: null, identityInHeaders: Identity(Admin, "household-admin"), message);
        var headerNarrowed = await StreamOverHubAsync(
            identityInQuery: null, identityInHeaders: Identity(Member, "household-member"), message);
        var queryNarrowed = await StreamOverHubAsync(
            identityInQuery: Identity(Member, "household-member"), identityInHeaders: null, message);

        // Control: the message really does reach the admin-only tool for a role that holds it, so
        // "no set_goal" below means RBAC filtered it — not that the Mock stopped routing.
        Assert.Equal(AdminOnlyTool, GatedTool(privileged));

        // RBAC-before-the-model held on the header transport, and the query-string transport now
        // reaches the identical outcome. Asserted as equality, not just "not set_goal": the point
        // is parity between two ways of carrying the same identity.
        Assert.NotEqual(AdminOnlyTool, GatedTool(queryNarrowed));
        Assert.Equal(GatedTool(headerNarrowed), GatedTool(queryNarrowed));
    }

    /// <summary>
    /// The third transport from the report: AG-UI over plain HTTP, identity in headers. Included so
    /// the comparison is the same one the defect was found with — three transports, one tenant, one
    /// role, one message — rather than a hub-only argument.
    /// </summary>
    [Fact]
    public async Task AgUiOverHttp_AgreesWithTheHubOverTheQueryString()
    {
        await EnsureTenantAsync();
        const string message = "set goal 4242";

        using var client = ClientFor(Member, "household-member");
        using var response = await client.PostAsJsonAsync("/api/agui/finance", new
        {
            messages = new[] { new { id = Guid.NewGuid().ToString("N"), role = "user", content = message } },
        });
        response.EnsureSuccessStatusCode();
        var sse = await response.Content.ReadAsStringAsync();

        var hub = await StreamOverHubAsync(
            identityInQuery: Identity(Member, "household-member"), identityInHeaders: null, message);

        Assert.DoesNotContain(AdminOnlyTool, sse, StringComparison.Ordinal);
        Assert.DoesNotContain("RUN_ERROR", sse, StringComparison.Ordinal);
        Assert.NotEqual(AdminOnlyTool, GatedTool(hub));
    }

    /// <summary>
    /// The escalation the fix could have introduced on its way to closing one. A query string is
    /// attacker-reachable in ways a header is not — it rides along in a link, a redirect, an
    /// <c>&lt;img src&gt;</c> — so promoting it must never let it OVERRIDE an identity the caller
    /// already sent in headers. Widened here to the whole role: the query claims
    /// <c>household-admin</c> while the header says <c>household-member</c>, and the header has to
    /// win.
    ///
    /// Red without the <c>Headers.ContainsKey</c> guard in <c>PromoteQueryIdentity</c>: the turn
    /// gates <c>set_goal</c>, a tool the header identity does not hold.
    /// </summary>
    [Fact]
    public async Task AQueryStringCannotOverrideAnIdentityAlreadySentInHeaders()
    {
        await EnsureTenantAsync();

        var events = await StreamOverHubAsync(
            identityInQuery: Identity(Member, "household-admin"),
            identityInHeaders: Identity(Member, "household-member"),
            message: "set goal 4242");

        Assert.NotEqual(AdminOnlyTool, GatedTool(events));
    }

    /// <summary>
    /// The escalation the fix could have re-introduced on its way to closing one. To the platform
    /// handler an ABSENT roles header means <c>system_admin</c> and a PRESENT-but-empty one means
    /// "explicitly role-less" — and <see cref="IHeaderDictionary"/> deletes a header assigned an
    /// empty value. So the naive promotion turns <c>?X-Dev-Roles=</c> back into <c>["*"]</c>: the
    /// same escalation, through the door that was just closed.
    ///
    /// Asserted through the hub rather than on the helper, because the helper is not what enforces
    /// it. Red without <c>DevHubIdentityShim.NoRoles</c>: the turn gates <c>set_goal</c>.
    /// </summary>
    [Fact]
    public async Task AnEmptyRolesQueryParameter_IsRoleLess_NotSystemAdmin()
    {
        await EnsureTenantAsync();

        var identity = Identity(Member, "household-member");
        identity["X-Dev-Roles"] = string.Empty;

        var events = await StreamOverHubAsync(
            identityInQuery: identity, identityInHeaders: null, message: "set goal 4242");

        Assert.NotEqual(AdminOnlyTool, GatedTool(events));
    }

    // ── the transports ────────────────────────────────────────────────────────

    /// <summary>
    /// One hub turn over a real WebSocket against the test host, draining the stream.
    ///
    /// <paramref name="identityInQuery"/> reproduces what a browser does: the upgrade request
    /// carries the <c>X-Dev-*</c> values as query parameters because a WebSocket handshake cannot
    /// set custom headers. <paramref name="identityInHeaders"/> is the control the browser cannot
    /// use but curl and the test host can.
    /// </summary>
    private async Task<IReadOnlyList<AgentStreamEvent>> StreamOverHubAsync(
        IReadOnlyDictionary<string, string>? identityInQuery,
        IReadOnlyDictionary<string, string>? identityInHeaders,
        string message)
    {
        var url = new UriBuilder(new Uri(fixture.Factory.Server.BaseAddress, "/hubs/agent"))
        {
            Query = identityInQuery is null
                ? string.Empty
                : string.Join('&', identityInQuery.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}")),
        }.Uri;

        var connection = new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                // WebSockets with no negotiate round trip: the single upgrade request is the whole
                // authentication surface, which is exactly the shape of the defect.
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var socketClient = fixture.Factory.Server.CreateWebSocketClient();
                    if (identityInHeaders is not null)
                    {
                        socketClient.ConfigureRequest = request =>
                        {
                            foreach (var (key, value) in identityInHeaders)
                            {
                                request.Headers[key] = value;
                            }
                        };
                    }

                    return await socketClient.ConnectAsync(context.Uri, cancellationToken);
                };
            })
            // RealtimeSetup on the server adds JsonStringEnumConverter to the hub protocol, so
            // AgentStreamEventType arrives as "ApprovalRequired", not 5. Without the matching
            // converter here every event fails to bind and the stream drains EMPTY — which reads
            // exactly like a passing negative assertion. Mirror the server.
            .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();

        await using (connection)
        {
            await connection.StartAsync();

            var request = new AgentRunRequest { ModuleId = FinanceModule.Id, Message = message };
            var events = new List<AgentStreamEvent>();
            await foreach (var evt in connection.StreamAsync<AgentStreamEvent>("Stream", request))
            {
                events.Add(evt);
            }

            Assert.DoesNotContain(events, e => e.Type == AgentStreamEventType.Error);

            // A turn that streamed nothing would make every "no set_goal" assertion below pass
            // vacuously, which is the exact failure mode a client/server protocol mismatch produces.
            Assert.Contains(events, e => e.Type == AgentStreamEventType.Completed);
            return events;
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>The tool the approval gate parked this turn on, or null if none was.</summary>
    private static string? GatedTool(IReadOnlyList<AgentStreamEvent> events) =>
        events.FirstOrDefault(e => e.Type == AgentStreamEventType.ApprovalRequired)?.ToolName;

    private static Dictionary<string, string> Identity(string subject, string role) => new()
    {
        ["X-Dev-Subject"] = subject,
        ["X-Dev-Tenant"] = Tenant,
        ["X-Dev-Roles"] = role,
    };

    private HttpClient ClientFor(string subject, string role, string? tenant = null)
    {
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", tenant ?? Tenant);
        client.DefaultRequestHeaders.Add("X-Dev-Roles", role);
        return client;
    }

    private async Task<int> ConversationCountAsync(string tenant, string subject, string role)
    {
        using var client = ClientFor(subject, role, tenant);
        var response = await client.GetAsync("/api/chat/conversations");
        response.EnsureSuccessStatusCode();
        var rows = await response.Content.ReadFromJsonAsync<List<ConversationRow>>();
        return rows?.Count ?? 0;
    }

    /// <summary>
    /// Provisions the household through the platform's own pipeline (tenant + admin + the finance
    /// module + the seeded role baselines), idempotently — the collection shares one host, so a
    /// second test in this class must find it already there rather than 409.
    /// </summary>
    private async Task EnsureTenantAsync()
    {
        using var admin = fixture.AdminClient();
        using var response = await admin.PostAsJsonAsync("/api/admin/tenants/provision", new
        {
            name = "Hub Identity Household",
            slug = Tenant,
            adminEmail = $"{Admin}@{Tenant}.local",
            adminSubject = Admin,
            adminDisplayName = "Hub Identity Admin",
            modules = new[] { FinanceModule.Id },
        });

        if (response.StatusCode is not (System.Net.HttpStatusCode.Created or System.Net.HttpStatusCode.Conflict))
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.Fail($"could not provision '{Tenant}': {(int)response.StatusCode} {body}");
        }
    }
}

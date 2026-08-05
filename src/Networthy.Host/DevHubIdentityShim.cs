using Microsoft.AspNetCore.Authentication;
using Plenipo.AspNetCore.Auth;

namespace Networthy.Host;

/// <summary>
/// TODO(plenipo#110): delete this file, and its call in Program.cs, once Plenipo's
/// <c>DevAuthenticationHandler</c> reads hub-path query identity itself.
///
/// Networthy #172. A browser's WebSocket upgrade cannot set custom headers, so
/// <c>@microsoft/signalr</c> moves the dev-auth <c>X-Dev-*</c> values into the query string when it
/// connects to <c>/hubs/agent</c>. Plenipo 0.1.0-alpha.28's <c>DevAuthenticationHandler</c> reads
/// <c>Request.Headers</c> only, so it never saw them and fell back to its defaults — subject
/// <c>dev-user</c>, tenant <c>dev</c>, and (roles header absent) <c>system_admin</c>, i.e. <c>["*"]</c>.
/// Every chat turn the shipped UI sends therefore ran in the WRONG HOUSEHOLD with permissions the
/// caller does not hold, and the pre-model-call tool filter offered tools RBAC should have removed.
///
/// The platform already encodes this exact rule for a configured IdP: <c>AuthSetup</c> accepts the
/// bearer from <c>?access_token</c> for <c>/hubs</c> paths only, because "a query string is logged by
/// proxies and kept in browser history". This shim applies the same restriction to the same
/// transport for the dev-auth branch — it grants nothing that a header does not already grant, and
/// narrows a principal that was otherwise <c>system_admin</c>.
///
/// Deliberately a blunt header rewrite rather than anything reusable: it should read as temporary.
/// </summary>
public static class DevHubIdentityShim
{
    /// <summary>
    /// The identity inputs <c>DevAuthenticationHandler</c> reads. Only these five are promoted —
    /// an allow-list, so the query string can never introduce some other header.
    /// </summary>
    private static readonly string[] DevIdentityHeaders =
        ["X-Dev-Subject", "X-Dev-Email", "X-Dev-Name", "X-Dev-Tenant", "X-Dev-Roles"];

    private const string RolesHeader = "X-Dev-Roles";

    /// <summary>
    /// To the platform handler a PRESENT-but-empty roles value is "an explicitly role-less
    /// principal", while an ABSENT one means <c>system_admin</c>. <see cref="IHeaderDictionary"/>
    /// DELETES a header assigned an empty value, so promoting <c>?X-Dev-Roles=</c> naively turns
    /// the former into the latter — an escalation to <c>["*"]</c>, which is the very bug this shim
    /// exists to close. A lone separator is the shortest non-empty value the handler's
    /// <c>Split(',', RemoveEmptyEntries)</c> reduces back to no roles.
    /// </summary>
    private const string NoRoles = ",";

    /// <summary>
    /// Must be registered BEFORE <c>UsePlenipoPlatform()</c> (which is what
    /// <c>RunPlenipoPlatformAsync()</c> calls): that is where the platform's own
    /// <c>UseAuthentication()</c> sits, and the headers have to be in place before the handler runs.
    /// </summary>
    public static WebApplication UseDevHubIdentityShim(this WebApplication app)
    {
        // Dev-auth only exists in Development — outside it, AddPlenipoAuthentication throws unless a
        // real IdP is configured, and that path has its own ?access_token support.
        if (!app.Environment.IsDevelopment())
        {
            return app;
        }

        app.Use(async (context, next) =>
        {
            // Hub paths only, mirroring AuthSetup's restriction on the JwtBearer branch: a query
            // string reaches proxy logs and browser history that a header never does, so this must
            // not widen to the REST surface.
            if (context.Request.Path.StartsWithSegments("/hubs", StringComparison.Ordinal)
                && await IsDevAuthActiveAsync(context))
            {
                PromoteQueryIdentity(context.Request);
            }

            await next();
        });

        return app;
    }

    /// <summary>
    /// True only when the Dev scheme is what will actually authenticate this request. A deployment
    /// that configures Auth:Authority/Audience authenticates with JwtBearer, which already reads
    /// <c>?access_token</c> for hub paths — this shim must not touch it.
    /// </summary>
    private static async Task<bool> IsDevAuthActiveAsync(HttpContext context)
    {
        var schemes = context.RequestServices.GetRequiredService<IAuthenticationSchemeProvider>();
        var scheme = await schemes.GetDefaultAuthenticateSchemeAsync();
        return scheme is not null
            && string.Equals(scheme.Name, DevAuthenticationHandler.SchemeName, StringComparison.Ordinal);
    }

    private static void PromoteQueryIdentity(HttpRequest request)
    {
        foreach (var name in DevIdentityHeaders)
        {
            // A real header always wins: curl and the long-polling transport can send one, and this
            // shim exists only for the transport that cannot.
            if (request.Headers.ContainsKey(name) || !request.Query.TryGetValue(name, out var value))
            {
                continue;
            }

            var text = value.ToString();
            if (text.Length == 0)
            {
                if (name == RolesHeader)
                {
                    request.Headers[name] = NoRoles;
                }

                // Every other empty value is indistinguishable from absent to the handler, which
                // falls back on whitespace — so leave it absent rather than inventing a header.
                continue;
            }

            request.Headers[name] = text;
        }
    }
}

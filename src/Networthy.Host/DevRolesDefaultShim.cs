namespace Networthy.Host;

/// <summary>
/// TODO(plenipo#167): delete this file, and its call in Program.cs, once Plenipo's
/// <c>DevAuthenticationHandler</c> lets a product decide what an absent <c>X-Dev-Roles</c> means.
///
/// Networthy #227 — the sibling case #217 left open. #213 established the invariant *a principal
/// with no roles must be granted nothing*, and #217 enforced it for a PRESENT-but-empty
/// <c>X-Dev-Roles</c> by clearing <c>Auth:DefaultRole</c>. A request that OMITS the header never
/// reaches that path: <c>DevAuthenticationHandler</c> reads absence as its <c>system_admin</c>
/// convenience default and hands the caller <c>["*"]</c> outright, so the boundary held for two of
/// the three ways a caller can assert no roles and not the third.
///
/// The platform's behaviour is deliberate and documented there as dev convenience, so this is a
/// product decision the way <c>Auth:DefaultRole</c> was — Networthy is the one asserting that
/// asking for nothing gets nothing, and this shim is what holds that line until the platform
/// offers a knob for it.
///
/// It NARROWS ONLY. The single value it can ever write is <see cref="NoRoles"/>, and only when the
/// header is absent, so no request can come out of it holding a permission it did not already
/// hold. That is what makes it safe to run ahead of authentication on every path.
///
/// Two cases it deliberately leaves alone:
/// <list type="bullet">
///   <item>a request that already carries the header — including an empty or whitespace one, which
///   is #217's case and already correct;</item>
///   <item>any deployment authenticating with a real IdP, where the Dev scheme is not registered
///   at all and <c>X-Dev-*</c> means nothing.</item>
/// </list>
/// </summary>
public static class DevRolesDefaultShim
{
    private const string RolesHeader = "X-Dev-Roles";

    /// <summary>
    /// The shortest value <c>DevAuthenticationHandler</c> reduces back to zero roles: it splits on
    /// <c>','</c> with <c>RemoveEmptyEntries</c>, so a lone separator is a PRESENT header asserting
    /// nothing. An empty string cannot be used — <see cref="IHeaderDictionary"/> deletes a header
    /// assigned one, which would put the request straight back into the case being fixed.
    /// Same sentinel, same reason, as <see cref="DevHubIdentityShim"/>.
    /// </summary>
    private const string NoRoles = ",";

    /// <summary>
    /// Must be registered BEFORE <c>RunPlenipoPlatformAsync()</c> — that is where the platform's
    /// <c>UseAuthentication()</c> sits, and the header has to be in place before the handler reads
    /// it — and AFTER <see cref="DevHubIdentityShim.UseDevHubIdentityShim"/>, which promotes the
    /// hub-path query identity into headers. Running ahead of it would default a SignalR connection
    /// to no-roles before its <c>?X-Dev-Roles=</c> ever got the chance to be promoted.
    /// </summary>
    public static WebApplication UseDevRolesDefaultShim(this WebApplication app)
    {
        // Dev-auth only exists in Development; elsewhere AddPlenipoAuthentication requires a real
        // IdP, and rewriting request headers there would be meddling with someone else's protocol.
        if (!app.Environment.IsDevelopment())
        {
            return app;
        }

        app.Use(async (context, next) =>
        {
            if (!context.Request.Headers.ContainsKey(RolesHeader)
                && await DevHubIdentityShim.IsDevAuthActiveAsync(context))
            {
                context.Request.Headers[RolesHeader] = NoRoles;
            }

            await next();
        });

        return app;
    }
}

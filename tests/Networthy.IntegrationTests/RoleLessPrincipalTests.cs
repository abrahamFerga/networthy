using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Networthy.IntegrationTests;

/// <summary>
/// The boundary the shipped "Newcomer — no roles" persona exists to demonstrate (issue #213).
///
/// A principal that asserts NO roles must be permission-less until an invite or an admin grant
/// lands. The platform's JIT provisioning (<c>RequestEnricher</c>) grants <c>Auth:DefaultRole</c>
/// to a first-touch principal whose token carries no roles, so whatever this product names there
/// is silently handed to every unscoped caller — which is a product decision, not a platform bug.
/// Networthy names nothing, and these tests are what holds that line.
///
/// Per-test subjects: the grant is decided ONCE, when the user row is first written, so a shared
/// subject would let the first test's provisioning mask the second's.
/// </summary>
[Collection("api")]
public sealed class RoleLessPrincipalTests(IntegrationFixture fixture)
{
    /// <summary>
    /// A client whose <c>X-Dev-Roles</c> is PRESENT but asserts no role — how a real IdP presents
    /// an unscoped principal. Omitting the header entirely is a DIFFERENT case: dev-auth reads
    /// absence as the system_admin convenience default (<c>["*"]</c>), which would prove nothing
    /// about RBAC.
    ///
    /// The value is a single space, not <c>""</c>, and that is load-bearing: HttpClient drops a
    /// header whose value is empty, silently turning this into the omitted-header case — measured,
    /// not assumed. Dev-auth splits the value with RemoveEmptyEntries|TrimEntries, so a space
    /// arrives as a present header carrying zero roles, which is exactly the principal under test.
    /// </summary>
    private HttpClient RoleLessClient(string subject)
    {
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Dev-Roles", " ");
        return client;
    }

    [Fact]
    public async Task RoleLessPrincipal_IsGrantedNothing()
    {
        using var client = RoleLessClient("it-roleless-permissions");

        var me = await client.GetFromJsonAsync<JsonElement>("/api/platform/me");
        var permissions = me.GetProperty("permissions").EnumerateArray()
            .Select(p => p.GetString()).ToList();

        // Before the fix this returned the full 22-entry household-member set.
        Assert.Empty(permissions);
    }

    [Theory]
    [InlineData("/api/finance/accounts")]
    [InlineData("/api/finance/transactions")]
    [InlineData("/api/finance/overview")]
    public async Task RoleLessPrincipal_CannotReadTheHousehold(string route)
    {
        using var client = RoleLessClient("it-roleless-reads");

        var response = await client.GetAsync(route);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// The control that isolates the empty string as the trigger: a NAMED unknown role was already
    /// powerless, so if this ever diverges from the role-less case again, the promotion is back.
    /// </summary>
    [Fact]
    public async Task NamedUnknownRole_IsEquallyPowerless()
    {
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Subject", "it-unknown-role");
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        client.DefaultRequestHeaders.Add("X-Dev-Roles", "no_such_role");

        var response = await client.GetAsync("/api/finance/accounts");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// The positive control, so "deny everyone" can never pass as a fix: a principal that DOES
    /// assert household-member still reads the household.
    /// </summary>
    [Fact]
    public async Task NamedHouseholdMember_StillReadsTheHousehold()
    {
        using var client = fixture.ClientFor("household-member");

        var response = await client.GetAsync("/api/finance/accounts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

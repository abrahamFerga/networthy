using System.Net.Http.Json;
using Networthy.Finance;
using Xunit;

namespace Networthy.IntegrationTests;

/// <summary>
/// The isolation question the kit's <c>PlenipoTenancyConformance.A_second_tenant_sees_nothing_on_the_read_surfaces</c>
/// asks of <c>/api/finance/categories</c>, asked in the form that is actually true of this surface.
///
/// <para>The kit probes every module data tab and requires a fresh tenant to read ZERO rows. That
/// holds for accounts, transactions, budgets and goals. It cannot hold for categories: issue #148
/// made the starter taxonomy arrive for EVERY household however it was created
/// (<see cref="FinanceCategorySeed"/> hangs off the <c>/api/finance</c> group), so the second
/// tenant's very first read of that endpoint seeds and returns its own twenty starter names. The
/// kit's assertion sees twenty rows and cannot tell whose they are.</para>
///
/// <para>This test tells whose they are, which is the security-relevant half: a category that
/// exists only in <c>dev</c> is invisible to another household, and the rows the other household
/// does see are exactly the starter taxonomy — no more. The kit's structural leg
/// (<c>Every_tenant_owned_entity_in_every_registered_DbContext_has_a_query_filter</c>) already
/// proves <see cref="Networthy.Finance.Persistence.Category"/> carries the TenantId filter; this
/// proves it end to end through the endpoint.</para>
/// </summary>
[Collection("api")]
public sealed class CategoryTenancyTests(IntegrationFixture fixture)
{
    private const string OtherTenant = "category-tenancy";

    private sealed record CategoryRow(string Name);

    [Fact]
    public async Task A_second_household_sees_its_own_starter_taxonomy_and_none_of_devs_own_categories()
    {
        await fixture.EnsureTenantAsync(OtherTenant);

        // A category that exists in `dev` and nowhere else — the leak detector.
        var devOnly = $"Kit Tenancy Probe {Guid.NewGuid():N}";
        using var devAdmin = fixture.AdminClient();
        using (var created = await devAdmin.PostAsJsonAsync("/api/finance/categories", new { name = devOnly }))
        {
            created.EnsureSuccessStatusCode();
        }

        var devNames = (await ReadCategoriesAsync(devAdmin)).Select(c => c.Name).ToArray();
        Assert.Contains(devOnly, devNames);

        using var other = fixture.ClientFor("system_admin", OtherTenant);
        var otherNames = (await ReadCategoriesAsync(other)).Select(c => c.Name).ToArray();

        // Not dev's row...
        Assert.DoesNotContain(devOnly, otherNames);
        // ...and nothing beyond what this household was seeded with on its own first read.
        Assert.Equal(
            FinanceCatalog.StarterCategories.OrderBy(n => n, StringComparer.Ordinal),
            otherNames.OrderBy(n => n, StringComparer.Ordinal));
    }

    private static async Task<IReadOnlyList<CategoryRow>> ReadCategoriesAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/finance/categories");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<CategoryRow>>() ?? [];
    }
}

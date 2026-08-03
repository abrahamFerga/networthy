using System.Net.Http.Json;
using Networthy.Finance;
using Xunit;

namespace Networthy.IntegrationTests;

/// <summary>
/// Issue #148: the starter category taxonomy seeded only for the bootstrap tenant, so every
/// household created afterwards had none — and with no categories, budgets and categorised
/// transactions both reject, which is the module's primary journey.
///
/// Driven through the same surface the defect was found on (create a tenant, then read its
/// categories) rather than by calling the hook directly, because the thing that was actually
/// broken was WHEN seeding runs, not what it writes.
/// </summary>
[Collection("api")]
public sealed class TenantProvisioningSeedTests(IntegrationFixture fixture)
{
    private sealed record CategoryRow(string Name);

    [Fact]
    public async Task A_provisioned_household_gets_the_starter_taxonomy()
    {
        const string slug = "seed-provisioned";
        using var admin = fixture.AdminClient();

        var created = await admin.PostAsJsonAsync("/api/admin/tenants/provision", new
        {
            name = "Provisioned Household",
            slug,
            adminEmail = "admin@seed-provisioned.local",
            adminSubject = "seed-provisioned-admin",
            adminDisplayName = "Seed Provisioned Admin",
            modules = new[] { FinanceModule.Id },
        });
        created.EnsureSuccessStatusCode();

        var categories = await ReadCategoriesAsync(slug);

        Assert.Equal(
            FinanceCatalog.StarterCategories.OrderBy(n => n),
            categories.Select(c => c.Name).OrderBy(n => n));
    }

    /// <summary>
    /// The bare admin create — the path the defect was reported on. It does NOT run the platform's
    /// provisioning pipeline, so the provisioned-hook never fires for it; the taxonomy has to
    /// arrive some other way or this household still cannot budget.
    /// </summary>
    [Fact]
    public async Task A_bare_created_household_gets_the_starter_taxonomy()
    {
        const string slug = "seed-bare";
        using var admin = fixture.AdminClient();

        var created = await admin.PostAsJsonAsync(
            "/api/admin/tenants",
            new { name = "Bare Created Household", slug });
        created.EnsureSuccessStatusCode();

        var categories = await ReadCategoriesAsync(slug);

        Assert.Equal(
            FinanceCatalog.StarterCategories.OrderBy(n => n),
            categories.Select(c => c.Name).OrderBy(n => n));
    }

    /// <summary>
    /// The bootstrap tenant already seeded at startup, so the request-time backstop must find it
    /// and do nothing — a second copy of the taxonomy would give every category picker duplicates.
    ///
    /// Asserts one-of-each rather than an exact total on purpose: `dev` is the shared fixture
    /// tenant and sibling tests in this collection add their own categories, so a count assertion
    /// here passes alone and fails in a full run.
    /// </summary>
    [Fact]
    public async Task The_bootstrap_tenant_is_not_seeded_twice()
    {
        var names = (await ReadCategoriesAsync("dev")).Select(c => c.Name).ToList();

        var duplicated = FinanceCatalog.StarterCategories
            .Where(starter => names.Count(n => n == starter) != 1)
            .ToList();

        Assert.Empty(duplicated);
    }

    private async Task<IReadOnlyList<CategoryRow>> ReadCategoriesAsync(string tenantSlug)
    {
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Subject", $"it-{tenantSlug}-admin");
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", tenantSlug);
        client.DefaultRequestHeaders.Add("X-Dev-Roles", "system_admin");

        var response = await client.GetAsync("/api/finance/categories");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<CategoryRow>>() ?? [];
    }
}

using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Networthy.Finance.Persistence;
using Plenipo.Core.Multitenancy;

namespace Networthy.Finance;

/// <summary>
/// The one implementation of "a household has its starter taxonomy" (issue #148), and the backstop
/// that guarantees it however the household came to exist.
///
/// <para>A tenant can appear three ways, and <see cref="FinanceModule.SeedAsync"/> only ever covered
/// the first: the bootstrap tenant at startup, <c>POST /api/admin/tenants/provision</c>, and the
/// bare <c>POST /api/admin/tenants</c> — which runs no provisioning pipeline at all. That last one
/// is the path #148 was reported on.</para>
///
/// <para>The platform's <c>AddPlenipoTenantProvisionedHook</c> seam covers the provision path, but
/// not the bare one, and it was measurably redundant once the filter below existed: removing the
/// hook left every test green, so it is not here. One mechanism, not two.</para>
///
/// <para>Rather than chase creation paths, <see cref="EnsureSeededFilterAsync"/> hangs off the
/// module's single <c>/api/finance</c> endpoint group, so the taxonomy is guaranteed before any
/// finance endpoint runs — all 33 category call sites sit downstream of it. The check is one query
/// per tenant per process, remembered by <see cref="CategorySeedTracker"/>.</para>
/// </summary>
internal static class FinanceCategorySeed
{
    /// <summary>
    /// Seeds the starter taxonomy for <paramref name="tenantId"/> unless it already has one.
    /// Names the tenant explicitly rather than relying on the ambient one, because two of its three
    /// callers run outside a request where the global query filter compares against nothing.
    /// Returns true when it actually wrote.
    /// </summary>
    internal static async Task<bool> EnsureAsync(
        FinanceDbContext db,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var alreadySeeded = await db.Categories
            .IgnoreQueryFilters()
            .AnyAsync(c => c.TenantId == tenantId, cancellationToken);
        if (alreadySeeded)
        {
            return false;
        }

        foreach (var name in FinanceCatalog.StarterCategories)
        {
            db.Categories.Add(new Category { TenantId = tenantId, Name = name });
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            // The unique (TenantId, Name) index is the arbiter: a concurrent first request seeded
            // between the check and the write. That is the outcome we wanted, so drop our copies
            // and carry on rather than failing the request that raced.
            foreach (var entry in db.ChangeTracker.Entries<Category>()
                         .Where(e => e.State == EntityState.Added)
                         .ToList())
            {
                entry.State = EntityState.Detached;
            }

            return false;
        }
    }

    /// <summary>
    /// Endpoint filter on the <c>/api/finance</c> group. Written as a delegate rather than
    /// <c>AddEndpointFilter&lt;T&gt;</c> because that form builds the filter from the root provider,
    /// which cannot hand out a scoped <see cref="FinanceDbContext"/>.
    /// </summary>
    internal static async ValueTask<object?> EnsureSeededFilterAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var services = context.HttpContext.RequestServices;
        var tenant = services.GetRequiredService<ITenantContext>();
        var tracker = services.GetRequiredService<CategorySeedTracker>();

        if (tenant.HasTenant)
        {
            var tenantId = tenant.RequireTenantId();
            if (tracker.NeedsCheck(tenantId))
            {
                var db = services.GetRequiredService<FinanceDbContext>();
                await EnsureAsync(db, tenantId, context.HttpContext.RequestAborted);
                tracker.MarkChecked(tenantId);
            }
        }

        return await next(context);
    }
}

/// <summary>
/// Remembers which households this process has already confirmed a taxonomy for, so the backstop
/// costs one query per tenant per process rather than one per request. Deliberately in-memory and
/// not distributed: being wrong only ever means one extra <c>AnyAsync</c>.
/// </summary>
public sealed class CategorySeedTracker
{
    private readonly ConcurrentDictionary<Guid, bool> _confirmed = new();

    public bool NeedsCheck(Guid tenantId) => !_confirmed.ContainsKey(tenantId);

    public void MarkChecked(Guid tenantId) => _confirmed[tenantId] = true;
}

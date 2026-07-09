using Cortex.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Networthy.Finance.Persistence;

/// <summary>
/// The Finance module's own database — co-located in the platform database under a dedicated
/// <c>finance</c> schema and migrated via the module's <c>MigrateAsync</c> hook. The same global
/// query-filter pattern the platform uses enforces tenant (household) isolation on every entity:
/// one household's data is structurally invisible to another's.
/// </summary>
public sealed class FinanceDbContext(
    DbContextOptions<FinanceDbContext> options,
    ITenantContext tenantContext) : DbContext(options)
{
    /// <summary>Connection shared with the platform database (separate schema).</summary>
    public const string ConnectionName = "cortex-platform";
    public const string Schema = "finance";

    public DbSet<Category> Categories => Set<Category>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Category>(b =>
        {
            b.ToTable("categories");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(100).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
            b.HasOne<Category>().WithMany().HasForeignKey(x => x.ParentCategoryId).OnDelete(DeleteBehavior.Restrict);
            b.HasQueryFilter(x => x.TenantId == tenantContext.TenantId);
        });
    }
}

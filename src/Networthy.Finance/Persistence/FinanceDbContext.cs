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
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<NetWorthSnapshot> NetWorthSnapshots => Set<NetWorthSnapshot>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<StatementImportBatch> ImportBatches => Set<StatementImportBatch>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Account>(b =>
        {
            b.ToTable("accounts");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.Property(x => x.Type).HasMaxLength(16).IsRequired();
            b.Property(x => x.InstitutionName).HasMaxLength(200);
            b.Property(x => x.MaskedAccountNumber).HasMaxLength(24);
            b.Property(x => x.CurrencyCode).HasMaxLength(3).IsRequired();
            b.Property(x => x.CachedBalance).HasPrecision(18, 2);
            b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
            b.HasQueryFilter(x => x.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<NetWorthSnapshot>(b =>
        {
            b.ToTable("net_worth_snapshots");
            b.HasKey(x => x.Id);
            b.Property(x => x.CurrencyCode).HasMaxLength(3).IsRequired();
            b.Property(x => x.NetWorth).HasPrecision(18, 2);
            b.HasIndex(x => new { x.TenantId, x.TakenOn, x.CurrencyCode }).IsUnique();
            b.HasQueryFilter(x => x.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<Transaction>(b =>
        {
            b.ToTable("transactions");
            b.HasKey(x => x.Id);
            b.Property(x => x.Amount).HasPrecision(18, 2);
            b.Property(x => x.CurrencyCode).HasMaxLength(3).IsRequired();
            b.Property(x => x.Description).HasMaxLength(500).IsRequired();
            b.Property(x => x.Direction).HasMaxLength(8).IsRequired();
            b.Property(x => x.Source).HasMaxLength(8).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.OccurredOn });
            b.HasIndex(x => x.AccountId);
            b.HasIndex(x => x.CategoryId);
            b.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.SetNull);
            b.HasQueryFilter(x => x.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<StatementImportBatch>(b =>
        {
            b.ToTable("statement_import_batches");
            b.HasKey(x => x.Id);
            b.Property(x => x.FileName).HasMaxLength(300).IsRequired();
            b.Property(x => x.Status).HasMaxLength(12).IsRequired();
            b.Property(x => x.FailureReason).HasMaxLength(1000);
            b.HasIndex(x => new { x.TenantId, x.Status });
            b.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
            b.HasQueryFilter(x => x.TenantId == tenantContext.TenantId);
        });

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

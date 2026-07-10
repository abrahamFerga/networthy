using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Networthy.Finance.Persistence;

namespace Networthy.Finance;

/// <summary>
/// Budget-period rollover (ARCH.md Background work): when a new month starts, households that
/// budgeted last month get the same targets copied forward — budgeting shouldn't reset to zero
/// on the 1st. Tenant-blind, idempotent (only categories/currencies with no target this month
/// are copied), daily tick so a missed 1st self-heals.
/// </summary>
public sealed class BudgetRolloverService(
    IServiceScopeFactory scopes,
    ILogger<BudgetRolloverService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    await SweepOnceAsync(scope.ServiceProvider, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Budget rollover sweep failed; retrying on the next tick.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown.
        }
    }

    /// <summary>One sweep: copy last month's targets into the current month wherever a household
    /// hasn't set one yet. Returns how many budget rows were created.</summary>
    public static async Task<int> SweepOnceAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var db = services.GetRequiredService<FinanceDbContext>();
        // Month boundaries are household-local: on the UTC 1st it may still be last month in
        // Mexico City — per-tenant "this month" keeps the rollover honest at the edges.
        var zones = await db.HouseholdSettings.IgnoreQueryFilters()
            .ToDictionaryAsync(s => s.TenantId, s => s.TimeZoneId, cancellationToken);

        var utcToday = DateOnly.FromDateTime(DateTime.UtcNow);
        var window = new DateOnly(utcToday.Year, utcToday.Month, 1).AddMonths(-2);
        var candidates = await db.Budgets.IgnoreQueryFilters()
            .Where(b => b.PeriodMonth >= window)
            .ToListAsync(cancellationToken);
        if (candidates.Count == 0)
        {
            return 0;
        }

        var written = 0;
        foreach (var tenantGroup in candidates.GroupBy(b => b.TenantId))
        {
            var tenantToday = HouseholdSettings.TodayIn(zones.GetValueOrDefault(tenantGroup.Key));
            var thisMonth = new DateOnly(tenantToday.Year, tenantToday.Month, 1);
            var lastMonth = thisMonth.AddMonths(-1);

            var previous = tenantGroup.Where(b => b.PeriodMonth == lastMonth).ToList();
            if (previous.Count == 0)
            {
                continue;
            }

            var present = tenantGroup
                .Where(b => b.PeriodMonth == thisMonth)
                .Select(b => (b.CategoryId, b.CurrencyCode))
                .ToHashSet();
            foreach (var budget in BudgetMath.RolloverPlan(previous, present))
            {
                db.Budgets.Add(new Budget
                {
                    TenantId = budget.TenantId,
                    CategoryId = budget.CategoryId,
                    PeriodMonth = thisMonth,
                    TargetAmount = budget.TargetAmount,
                    CurrencyCode = budget.CurrencyCode,
                });
                written++;
            }
        }

        if (written > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return written;
    }
}

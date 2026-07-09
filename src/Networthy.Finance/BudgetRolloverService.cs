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
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        var lastMonth = thisMonth.AddMonths(-1);

        var previous = await db.Budgets.IgnoreQueryFilters()
            .Where(b => b.PeriodMonth == lastMonth)
            .ToListAsync(cancellationToken);
        if (previous.Count == 0)
        {
            return 0;
        }

        var current = await db.Budgets.IgnoreQueryFilters()
            .Where(b => b.PeriodMonth == thisMonth)
            .Select(b => new { b.TenantId, b.CategoryId, b.CurrencyCode })
            .ToListAsync(cancellationToken);

        var written = 0;
        foreach (var tenantGroup in previous.GroupBy(b => b.TenantId))
        {
            var present = current
                .Where(c => c.TenantId == tenantGroup.Key)
                .Select(c => (c.CategoryId, c.CurrencyCode))
                .ToHashSet();
            foreach (var budget in BudgetMath.RolloverPlan([.. tenantGroup], present))
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

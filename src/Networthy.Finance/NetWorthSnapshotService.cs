using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Networthy.Finance.Persistence;

namespace Networthy.Finance;

/// <summary>
/// The daily net-worth snapshot behind the trend view (ARCH.md Background work): one row per
/// household per currency per day, computed from account balances. Sweeps run tenant-blind
/// (IgnoreQueryFilters — no ambient tenant in the background) and are idempotent per day, so
/// the 6-hour tick cadence just narrows how late in the day the first snapshot lands.
/// </summary>
public sealed class NetWorthSnapshotService(
    IServiceScopeFactory scopes,
    ILogger<NetWorthSnapshotService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        try
        {
            // First tick after one full interval: startup work first, and tests/verification can
            // drive SweepOnceAsync deterministically without racing the service.
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    await SweepOnceAsync(scope.ServiceProvider, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Net-worth snapshot sweep failed; retrying on the next tick.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown.
        }
    }

    /// <summary>One sweep: for every household with accounts, write today's per-currency snapshot
    /// if it doesn't exist yet. Returns how many snapshot rows were written.</summary>
    public static async Task<int> SweepOnceAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var db = services.GetRequiredService<FinanceDbContext>();
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);

        var totals = await db.Accounts.IgnoreQueryFilters()
            .GroupBy(a => new { a.TenantId, a.CurrencyCode })
            .Select(g => new { g.Key.TenantId, g.Key.CurrencyCode, Total = g.Sum(a => a.CachedBalance) })
            .ToListAsync(cancellationToken);

        var existing = await db.NetWorthSnapshots.IgnoreQueryFilters()
            .Where(s => s.TakenOn == today)
            .Select(s => new { s.TenantId, s.CurrencyCode })
            .ToListAsync(cancellationToken);
        var seen = existing.Select(e => (e.TenantId, e.CurrencyCode)).ToHashSet();

        var written = 0;
        foreach (var t in totals)
        {
            if (seen.Contains((t.TenantId, t.CurrencyCode)))
            {
                continue;
            }

            db.NetWorthSnapshots.Add(new NetWorthSnapshot
            {
                TenantId = t.TenantId,
                TakenOn = today,
                CurrencyCode = t.CurrencyCode,
                NetWorth = t.Total,
            });
            written++;
        }

        if (written > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return written;
    }
}

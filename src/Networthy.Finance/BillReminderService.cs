using Cortex.Application.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Networthy.Finance.Persistence;

namespace Networthy.Finance;

/// <summary>
/// Turns detection into a heads-up: when a detected recurring charge is expected within the
/// next three days, the household member who logged it gets a notification (in-app baseline,
/// plus whatever channels the deployment registered). A tenant-blind sweep — query filters off,
/// tenant ids explicit — and idempotent: each (merchant, expected date) reminds exactly once,
/// tracked in the bill_reminders table.
/// </summary>
public sealed class BillReminderService(
    IServiceScopeFactory scopes,
    ILogger<BillReminderService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(12);

    internal const int HeadsUpDays = 3;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        try
        {
            // First tick after one full interval — tests drive SweepOnceAsync deterministically.
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    await SweepOnceAsync(scope.ServiceProvider, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Bill-reminder sweep failed; retrying on the next tick.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown.
        }
    }

    /// <summary>One sweep across every tenant. Returns how many reminders were sent.</summary>
    public static async Task<int> SweepOnceAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var db = services.GetRequiredService<FinanceDbContext>();
        var notifier = services.GetRequiredService<INotifier>();
        var settingsByTenant = await db.HouseholdSettings.IgnoreQueryFilters()
            .ToDictionaryAsync(s => s.TenantId, cancellationToken);
        var utcToday = DateOnly.FromDateTime(DateTime.UtcNow);
        var since = utcToday.AddDays(-RecurringTools.LookbackDays - 2);

        var expenses = await db.Transactions.IgnoreQueryFilters()
            .Where(t => t.Direction == "expense" && t.OccurredOn >= since)
            .ToListAsync(cancellationToken);
        var alreadySent = (await db.BillReminders.IgnoreQueryFilters()
                .Where(r => r.ExpectedOn >= utcToday.AddDays(-1))
                .ToListAsync(cancellationToken))
            .Select(r => (r.TenantId, r.MerchantKey, r.ExpectedOn))
            .ToHashSet();

        var sent = 0;
        foreach (var tenantGroup in expenses.GroupBy(t => t.TenantId))
        {
            var settings = settingsByTenant.GetValueOrDefault(tenantGroup.Key);
            var today = HouseholdSettings.TodayIn(settings?.TimeZoneId);
            var leadDays = settings?.BillReminderLeadDays ?? HeadsUpDays;
            var charges = RecurringDetection.Detect(tenantGroup.Select(
                t => new RecurringDetection.Observation(t.OccurredOn, t.Amount, t.Description)));

            foreach (var charge in charges.Where(c =>
                         c.NextExpected >= today && c.NextExpected <= today.AddDays(leadDays)))
            {
                if (alreadySent.Contains((tenantGroup.Key, charge.MerchantKey, charge.NextExpected)))
                {
                    continue;
                }

                // The member who logged the charge's latest occurrence gets the heads-up.
                var recipient = tenantGroup
                    .Where(t => RecurringDetection.NormalizeMerchant(t.Description) == charge.MerchantKey)
                    .OrderByDescending(t => t.OccurredOn)
                    .Select(t => t.CreatedByUserId)
                    .FirstOrDefault(id => id is not null);
                if (recipient is null)
                {
                    continue; // imported with no owner — nothing sensible to target
                }

                await notifier.NotifyAsync(new Notification(
                    tenantGroup.Key,
                    recipient.Value,
                    Category: "finance.bill",
                    Title: $"{charge.DisplayName} expected ≈{charge.NextExpected:MMM d}",
                    Body: $"Usually {charge.AverageAmount:N2} {charge.Cadence}" +
                          (charge.PriceRisen ? $" — the last charge ({charge.LastAmount:N2}) was above average." : "."),
                    Link: "/finance/recurring"), cancellationToken);

                db.BillReminders.Add(new BillReminder
                {
                    TenantId = tenantGroup.Key,
                    MerchantKey = charge.MerchantKey,
                    ExpectedOn = charge.NextExpected,
                });
                sent++;
            }
        }

        if (sent > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return sent;
    }
}

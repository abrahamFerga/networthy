using Cortex.Application.Jobs;
using Cortex.Application.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Networthy.Finance.Persistence;

namespace Networthy.Finance;

/// <summary>
/// The household's daily digest (issue #48), on the platform's recurring-job seam: once a day per
/// tenant, LOW-PRIORITY alert classes are batched into at most one notification per category per
/// member — never one notification per event. Time-sensitive classes stay per-event elsewhere:
/// bill reminders keep <see cref="BillReminderService"/> (a bill due tomorrow shouldn't wait for
/// tomorrow's digest) and approval requests are emitted by the platform the moment they queue.
///
/// Recipients are the members the module can actually see — the distinct authors of the
/// household's transactions — and each member's digest respects their account visibility, so a
/// private account's overspend never leaks into another member's inbox. Members who never wrote
/// a transaction aren't derivable from finance data; enumerating them needs a platform user
/// directory, tracked as a follow-up seam.
///
/// Two runs landing on the same day (the seam's catch-up-one after downtime) recompute the same
/// content — a rare duplicate digest, never a wrong one.
/// </summary>
public sealed class DailyDigestJobHandler : IJobHandler
{
    public const string JobKind = "finance.daily-digest";

    public string Kind => JobKind;

    public async Task<string?> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        var services = context.ScopedServices;
        var db = services.GetRequiredService<FinanceDbContext>();
        var notifier = services.GetRequiredService<INotifier>();

        // The seam restores the tenant, so — unlike the old hosted sweeps — every query here runs
        // with the normal tenant filters on. No IgnoreQueryFilters, no cross-tenant grouping.
        var settings = await db.HouseholdSettings.FirstOrDefaultAsync(cancellationToken);
        var today = settings?.Today() ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var period = new DateOnly(today.Year, today.Month, 1);
        var currencyCode = settings?.DefaultCurrencyCode ?? "USD";

        var accounts = await db.Accounts.ToListAsync(cancellationToken);
        var budgets = await db.Budgets.Where(b => b.PeriodMonth == period).ToListAsync(cancellationToken);
        var categoryNames = await db.Categories.ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        var monthExpenses = await db.Transactions
            .Where(t => t.Direction == "expense" && t.OccurredOn >= period && t.OccurredOn <= today)
            .ToListAsync(cancellationToken);

        var recipients = await db.Transactions
            .Where(t => t.CreatedByUserId != null)
            .Select(t => t.CreatedByUserId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

        var sent = 0;
        foreach (var userId in recipients)
        {
            var visibleIds = accounts.Where(a => a.IsVisibleTo(userId)).Select(a => a.Id).ToHashSet();

            var overBudget = DailyDigest.OverBudget(
                budgets, monthExpenses.Where(t => visibleIds.Contains(t.AccountId)), categoryNames, currencyCode);
            if (overBudget.Count > 0)
            {
                await notifier.NotifyAsync(new Notification(
                    context.TenantId, userId,
                    Category: "finance.budgets",
                    Title: DailyDigest.OverBudgetTitle(overBudget.Count),
                    Body: string.Join(" · ", overBudget.Select(o =>
                        $"{o.CategoryName}: over by {o.OverBy:N2} {currencyCode}")),
                    Link: "/finance/budgets"), cancellationToken);
                sent++;
            }

            var charges = await RecurringTools.DetectAsync(db, userId, today, cancellationToken);
            var newlyDetected = DailyDigest.NewlyDetected(charges, today);
            if (newlyDetected.Count > 0)
            {
                await notifier.NotifyAsync(new Notification(
                    context.TenantId, userId,
                    Category: "finance.recurring",
                    Title: DailyDigest.NewlyDetectedTitle(newlyDetected.Count),
                    Body: string.Join(" · ", newlyDetected.Select(c =>
                        $"{c.DisplayName} ≈{c.AverageAmount:N2} {c.Cadence}")),
                    Link: "/finance/recurring"), cancellationToken);
                sent++;
            }
        }

        return $"{{\"recipients\":{recipients.Count},\"notifications\":{sent}}}";
    }
}

/// <summary>Pure digest composition, unit-tested without a database.</summary>
public static class DailyDigest
{
    /// <summary>
    /// A newly detected recurring charge just crossed the detector's minimum-occurrence threshold
    /// with its LATEST charge — i.e. it was invisible yesterday. Stateless on purpose (no
    /// "already told you" table): the occurrence count equals the detection minimum and the last
    /// occurrence is within the freshness window, so a charge surfaces in exactly one digest
    /// window and never re-announces as its history grows.
    /// </summary>
    internal const int DetectionMinimum = 3;

    /// <summary>Days after <c>LastSeen</c> during which a threshold-crossing charge counts as new.</summary>
    internal const int FreshnessDays = 2;

    public sealed record OverBudgetLine(string CategoryName, decimal OverBy);

    /// <summary>
    /// The month's over-budget categories in the household currency, computed from exactly the
    /// same rows the Budgets tab and safe-to-spend read — the digest can never disagree with the
    /// dashboard about what "over" means.
    /// </summary>
    public static IReadOnlyList<OverBudgetLine> OverBudget(
        IEnumerable<Budget> budgets,
        IEnumerable<Transaction> visibleMonthExpenses,
        IReadOnlyDictionary<Guid, string> categoryNames,
        string currencyCode)
    {
        var expenses = visibleMonthExpenses.ToList();
        return [.. budgets
            .Where(b => b.CurrencyCode.Equals(currencyCode, StringComparison.OrdinalIgnoreCase))
            .Select(b => new
            {
                Name = categoryNames.GetValueOrDefault(b.CategoryId, "(deleted)"),
                Spent = expenses
                    .Where(t => t.CategoryId == b.CategoryId &&
                                t.CurrencyCode.Equals(b.CurrencyCode, StringComparison.OrdinalIgnoreCase))
                    .Sum(t => t.Amount),
                b.TargetAmount,
            })
            .Where(x => BudgetMath.Status(x.TargetAmount, x.Spent).Over)
            .OrderByDescending(x => x.Spent - x.TargetAmount)
            .Select(x => new OverBudgetLine(x.Name, x.Spent - x.TargetAmount))];
    }

    /// <summary>Charges that just became detectable — see <see cref="DetectionMinimum"/>.</summary>
    public static IReadOnlyList<RecurringCharge> NewlyDetected(
        IEnumerable<RecurringCharge> charges, DateOnly today) =>
        [.. charges.Where(c =>
            c.Occurrences == DetectionMinimum &&
            c.LastSeen >= today.AddDays(-FreshnessDays))];

    public static string OverBudgetTitle(int count) =>
        count == 1 ? "1 category is over budget" : $"{count} categories are over budget";

    public static string NewlyDetectedTitle(int count) =>
        count == 1 ? "New recurring charge detected" : $"{count} new recurring charges detected";
}

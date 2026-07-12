using Cortex.Application.Jobs;
using Cortex.Application.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Networthy.Finance.Persistence;

namespace Networthy.Finance;

/// <summary>
/// The household's recurring statement-upload reminder (issue #71), on the platform's
/// recurring-job seam — the sibling of <see cref="DailyDigestJobHandler"/>. Where the digest
/// batches LOW-PRIORITY alert classes, this is a single ACTIONABLE nudge: "a new period started;
/// bring in a statement." The scheduler ticks daily, but the reminder fires once per
/// household-configured period (monthly by default) — a per-tenant, per-period marker
/// (<see cref="StatementReminder"/>) makes a same-period catch-up-one run after downtime recompute
/// the same content without a duplicate nudge (the digest tolerates a rare duplicate; this one
/// persists a marker so it never sends one).
///
/// Recipients are the members the module can actually see — the distinct authors of the
/// household's transactions, exactly as the digest derives them — and each member's nudge respects
/// their account visibility, so a private account's income never shapes another member's reminder.
///
/// The nuance (issue #71): when the household's declared income (an <see cref="IncomeSource"/>) has
/// been arriving steadily, the nudge offers a one-click CONFIRM / roll-forward path instead of a
/// hard upload gate — a convenience for consistent recurring income only. A genuinely irregular
/// period falls back to the normal upload -> review_import_batch -> approve_import_batch flow,
/// whose two approval gates this path never bypasses (it posts nothing; it only spares the upload).
/// </summary>
public sealed class StatementReminderJobHandler : IJobHandler
{
    public const string JobKind = "finance.statement-reminder";

    public string Kind => JobKind;

    public async Task<string?> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        var services = context.ScopedServices;
        var db = services.GetRequiredService<FinanceDbContext>();
        var notifier = services.GetRequiredService<INotifier>();

        // The seam restores the tenant, so — like DailyDigestJobHandler — every query here runs
        // with the normal tenant filters on. No IgnoreQueryFilters, no cross-tenant grouping.
        var settings = await db.HouseholdSettings.FirstOrDefaultAsync(cancellationToken);
        if (settings is not null && !settings.StatementRemindersEnabled)
        {
            return "{\"enabled\":false}"; // the household turned the reminder off
        }

        var today = settings?.Today() ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var cadence = HouseholdSettings.NormalizeStatementCadence(settings?.StatementReminderCadence) ?? "monthly";
        var currencyCode = settings?.DefaultCurrencyCode ?? "USD";
        var period = StatementReminderPlan.PeriodStart(today, cadence);

        // Idempotency: a marker for this period means every eligible member was already nudged, so a
        // same-period catch-up run is a no-op — never a duplicate.
        if (await db.StatementReminders.AnyAsync(r => r.PeriodStart == period, cancellationToken))
        {
            return "{\"alreadyNudged\":true}";
        }

        // The period only just started, so "consistent income" is judged on the PREVIOUS, completed
        // period: did the declared income actually arrive as expected? If so, we can offer to roll
        // it forward rather than demand a fresh upload for a household whose inflows never surprise.
        var previousStart = StatementReminderPlan.PreviousPeriodStart(period, cadence);
        var previousEnd = period.AddDays(-1);
        var incomeFactor = StatementReminderPlan.PeriodIncomeFactor(cadence);

        var accounts = await db.Accounts.ToListAsync(cancellationToken);
        var incomeSources = await db.IncomeSources
            .Where(i => i.CurrencyCode == currencyCode)
            .ToListAsync(cancellationToken);
        var previousIncome = await db.Transactions
            .Where(t => t.Direction == "income" && t.CurrencyCode == currencyCode &&
                        t.OccurredOn >= previousStart && t.OccurredOn <= previousEnd)
            .ToListAsync(cancellationToken);

        var recipients = await db.Transactions
            .Where(t => t.CreatedByUserId != null)
            .Select(t => t.CreatedByUserId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (recipients.Count == 0)
        {
            // No members are derivable from finance data yet — leave no marker so the first member
            // to record a transaction still gets this period's nudge.
            return "{\"recipients\":0,\"notifications\":0}";
        }

        var sent = 0;
        var autoConfirmed = 0;
        foreach (var userId in recipients)
        {
            var visibleIds = accounts.Where(a => a.IsVisibleTo(userId)).Select(a => a.Id).ToHashSet();

            // A member's decision is scoped to what they can see: declared income landing in a
            // visible (or unlinked) account, and income that actually arrived in visible accounts.
            var expected = incomeSources
                .Where(i => i.AccountId is not { } acct || visibleIds.Contains(acct))
                .Sum(i => i.MonthlyEquivalent) * incomeFactor;
            var actual = previousIncome
                .Where(t => visibleIds.Contains(t.AccountId))
                .Sum(t => t.Amount);

            var consistent = StatementReminderPlan.IncomeIsConsistent(expected, actual);
            var notice = StatementReminderPlan.Compose(period, cadence, consistent, expected, currencyCode);

            await notifier.NotifyAsync(new Notification(
                context.TenantId, userId,
                Category: notice.Category,
                Title: notice.Title,
                Body: notice.Body,
                Link: notice.Link), cancellationToken);
            sent++;
            if (consistent)
            {
                autoConfirmed++;
            }
        }

        db.StatementReminders.Add(new StatementReminder
        {
            TenantId = context.TenantId,
            PeriodStart = period,
        });
        await db.SaveChangesAsync(cancellationToken);

        return $"{{\"recipients\":{recipients.Count},\"notifications\":{sent},\"autoConfirm\":{autoConfirmed}}}";
    }
}

/// <summary>Pure reminder composition, unit-tested without a database (mirrors <see cref="DailyDigest"/>).</summary>
public static class StatementReminderPlan
{
    /// <summary>
    /// The notification category actionable statement nudges publish under — its own class,
    /// deliberately NOT one of the digest's low-priority categories, so a member can mute the
    /// statement reminder without silencing budget or recurring-charge alerts (and vice versa).
    /// </summary>
    public const string Category = "finance.statements";

    /// <summary>
    /// How close the previous period's actual income must land to the declared expectation to count
    /// as "consistent" — 15%, absorbing an off-cycle bonus or a payday that slid a day across the
    /// boundary without ever calling a genuinely irregular month steady.
    /// </summary>
    public const decimal IncomeConsistencyTolerance = 0.15m;

    /// <summary>The first day of the period <paramref name="today"/> falls in, per cadence.</summary>
    public static DateOnly PeriodStart(DateOnly today, string cadence) => cadence == "weekly"
        ? today.AddDays(-(((int)today.DayOfWeek + 6) % 7)) // back to this week's Monday
        : new DateOnly(today.Year, today.Month, 1);

    /// <summary>The start of the period immediately before <paramref name="periodStart"/>.</summary>
    public static DateOnly PreviousPeriodStart(DateOnly periodStart, string cadence) => cadence == "weekly"
        ? periodStart.AddDays(-7)
        : periodStart.AddMonths(-1);

    /// <summary>Fraction of a monthly income a single period is expected to bring in (weekly ≈ 12/52).</summary>
    public static decimal PeriodIncomeFactor(string cadence) => cadence == "weekly" ? 12m / 52m : 1m;

    /// <summary>
    /// Income is "consistent" only when the household declared a positive expectation AND the
    /// previous period's inflows actually landed within tolerance of it. No declaration
    /// (<paramref name="expectedPeriodIncome"/> ≤ 0) or a period with no inflow at all
    /// (<paramref name="actualPeriodIncome"/> ≤ 0) is never consistent — there is nothing steady to
    /// roll forward, so the member is asked to upload.
    /// </summary>
    public static bool IncomeIsConsistent(decimal expectedPeriodIncome, decimal actualPeriodIncome,
        decimal tolerance = IncomeConsistencyTolerance)
    {
        if (expectedPeriodIncome <= 0 || actualPeriodIncome <= 0)
        {
            return false;
        }

        return Math.Abs(actualPeriodIncome - expectedPeriodIncome) / expectedPeriodIncome <= tolerance;
    }

    public sealed record Notice(bool AutoConfirm, string Category, string Title, string Body, string Link);

    /// <summary>
    /// The nudge's content: a one-click CONFIRM path when income is steady (link to the Income tab,
    /// no upload), or the upload -> review -> approve prompt when it isn't (link to Statement
    /// review). The confirm path posts nothing — it only spares a steady household the upload; the
    /// import approval gates stay exactly where they were.
    /// </summary>
    public static Notice Compose(DateOnly periodStart, string cadence, bool incomeConsistent,
        decimal expectedPeriodIncome, string currencyCode)
    {
        var periodLabel = cadence == "weekly"
            ? $"the week of {periodStart:MMM d}"
            : periodStart.ToString("MMMM yyyy");

        return incomeConsistent
            ? new Notice(
                AutoConfirm: true,
                Category: Category,
                Title: $"Confirm {periodLabel}'s income",
                Body: $"Your income has been steady (≈{expectedPeriodIncome:N2} {currencyCode}). " +
                      "Confirm to roll it forward — no statement upload needed this period.",
                Link: "/finance/income")
            : new Notice(
                AutoConfirm: false,
                Category: Category,
                Title: $"Upload {periodLabel}'s statement",
                Body: "Time to bring in a fresh bank statement: upload it, review the extracted " +
                      "lines, then approve — nothing posts until you do.",
                Link: "/finance/review");
    }
}

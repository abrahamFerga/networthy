using System.ComponentModel;
using System.Text;
using Plenipo.Core.Identity;
using Microsoft.EntityFrameworkCore;
using Networthy.Finance.Persistence;

namespace Networthy.Finance;

/// <summary>
/// "What subscriptions do we have?" — detection over the household's own expense history
/// (last ~13 months, visible accounts), computed on demand. Conservative by design; the
/// answer names its own limits when nothing qualifies.
/// </summary>
public sealed class RecurringTools(
    FinanceDbContext db,
    ICurrentUser currentUser,
    HouseholdContext household)
{
    internal const int LookbackDays = 400;

    [Description("Detected recurring charges — subscriptions, bills, memberships — with cadence, average amount, total monthly cost, price-rise flags, and what's due soon. Computed from your own transactions; read-only.")]
    public async Task<string> ListRecurring(CancellationToken cancellationToken = default)
    {
        var today = await household.TodayAsync(cancellationToken);
        var charges = await DetectAsync(db, currentUser.UserId, today, cancellationToken);
        if (charges.Count == 0)
        {
            return "No recurring charges detected yet. Detection needs at least three same-merchant " +
                   "charges at a steady rhythm — import more statement history and ask again.";
        }

        var sb = new StringBuilder($"Recurring charges ({charges.Count} detected):\n");
        foreach (var charge in charges)
        {
            sb.Append($"- {charge.DisplayName}: {charge.AverageAmount:N2} {charge.Cadence} " +
                      $"(≈{charge.MonthlyCost:N2}/month, seen {charge.Occurrences}×, next ≈{charge.NextExpected:yyyy-MM-dd})");
            if (charge.PriceRisen)
            {
                sb.Append($" ⚠ latest charge {charge.LastAmount:N2} is above the {charge.AverageAmount:N2} average — price rise?");
            }

            sb.AppendLine();
        }

        sb.AppendLine($"Total: ≈{charges.Sum(c => c.MonthlyCost):N2}/month.");

        var soon = charges.Where(c => c.NextExpected <= today.AddDays(7)).ToList();
        if (soon.Count > 0)
        {
            sb.Append($"Due within a week: {string.Join(", ", soon.Select(c => $"{c.DisplayName} (~{c.NextExpected:MMM d})"))}.");
        }

        return sb.ToString();
    }

    /// <summary>Detection over the caller-visible expense history. Shared with the tab endpoint.</summary>
    internal static async Task<IReadOnlyList<RecurringCharge>> DetectAsync(
        FinanceDbContext db, Guid? userId, DateOnly today, CancellationToken cancellationToken)
    {
        var since = today.AddDays(-LookbackDays);
        var visibleIds = (await db.Accounts.ToListAsync(cancellationToken))
            .Where(a => a.IsVisibleTo(userId))
            .Select(a => a.Id)
            .ToHashSet();
        var expenses = (await db.Transactions
                .Where(t => t.Direction == "expense" && t.OccurredOn >= since)
                .ToListAsync(cancellationToken))
            .Where(t => visibleIds.Contains(t.AccountId))
            .Select(t => new RecurringDetection.Observation(t.OccurredOn, t.Amount, t.Description));
        return RecurringDetection.Detect(expenses);
    }
}

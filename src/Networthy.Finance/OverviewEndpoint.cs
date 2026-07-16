using Plenipo.Application.Authorization;
using Plenipo.Core.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Networthy.Finance.Persistence;

namespace Networthy.Finance;

/// <summary>
/// The safe-to-spend figure, computed in ONE place: the dashboard's hero number and (epic 11)
/// the chat assistant's explanation must be the same number, so the formula lives here and
/// nowhere else. Deliberately conservative and deterministic: the sum of what's LEFT in this
/// month's budgets — an over-budget category contributes zero, never a negative that would
/// hide headroom elsewhere. No budgets at all means there is no honest number to show, so the
/// answer is null rather than a fabrication (the UI renders guidance instead).
/// </summary>
public static class SafeToSpendMath
{
    public sealed record SafeToSpend(decimal Amount, decimal TotalTarget, decimal TotalSpent, int BudgetCount);

    /// <summary>Σ max(0, target − spent) over the month's budgets; null when there are none.</summary>
    public static SafeToSpend? Compute(IReadOnlyList<(decimal Target, decimal Spent)> budgets) =>
        budgets.Count == 0
            ? null
            : new SafeToSpend(
                budgets.Sum(b => Math.Max(0m, b.Target - b.Spent)),
                budgets.Sum(b => b.Target),
                budgets.Sum(b => b.Spent),
                budgets.Count);
}

/// <summary>
/// The Overview tab's single composed read: one GET, one payload, every figure traceable to the
/// same queries the individual tabs run (budgets/spent mirrors the budgets tab, the net-worth
/// current net worth honors account visibility, upcoming bills mirror the recurring tab). Composed server-side
/// so the dashboard can't drift from the tabs it summarizes.
/// </summary>
internal static class OverviewEndpoint
{
    internal static void MapOverviewEndpoint(this IEndpointRouteBuilder group)
    {
        ((RouteGroupBuilder)group).MapGet("/overview", async (
                FinanceDbContext db, ICurrentUser currentUser, HouseholdContext household,
                CancellationToken cancellationToken) =>
            {
                var today = await household.TodayAsync(cancellationToken);
                var currencyCode = await household.ResolveCurrencyAsync(null, cancellationToken);
                var period = new DateOnly(today.Year, today.Month, 1);
                var monthEnd = period.AddMonths(1).AddDays(-1);

                var accounts = (await db.Accounts.ToListAsync(cancellationToken))
                    .Where(a => a.IsVisibleTo(currentUser.UserId))
                    .ToList();
                var visibleIds = accounts.Select(a => a.Id).ToHashSet();
                var categoryNames = await db.Categories.ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);

                // ── Budgets + safe-to-spend (same spent computation as the budgets tab) ──
                var budgets = await db.Budgets.Where(b => b.PeriodMonth == period).ToListAsync(cancellationToken);
                var monthExpenses = (await db.Transactions
                        .Where(t => t.Direction == "expense" && t.OccurredOn >= period && t.OccurredOn <= monthEnd)
                        .ToListAsync(cancellationToken))
                    .Where(t => visibleIds.Contains(t.AccountId))
                    .ToList();
                var budgetRows = budgets.Select(b => new
                {
                    categoryName = categoryNames.GetValueOrDefault(b.CategoryId, "(deleted)"),
                    spent = monthExpenses
                            .Where(t => t.CategoryId == b.CategoryId &&
                                        t.CurrencyCode.Equals(b.CurrencyCode, StringComparison.OrdinalIgnoreCase))
                            .Sum(t => t.Amount),
                    target = b.TargetAmount,
                    currencyCode = b.CurrencyCode,
                })
                    .OrderByDescending(x => x.target)
                    .ToList();
                var safeToSpend = SafeToSpendMath.Compute(
                    budgetRows.Where(b => b.currencyCode.Equals(currencyCode, StringComparison.OrdinalIgnoreCase))
                        .Select(b => (b.target, b.spent)).ToList());

                // ── Net worth: live total from visible accounts. Tenant-wide snapshots are
                // admin-only because their aggregates can include another member's private account. ──
                var netWorthTotal = accounts
                    .Where(a => a.CurrencyCode.Equals(currencyCode, StringComparison.OrdinalIgnoreCase))
                    .Sum(a => a.CachedBalance);
                IReadOnlyList<decimal> trend = [];

                // ── Upcoming bills (same detection the recurring tab runs), soonest first ──
                var upcoming = (await RecurringTools.DetectAsync(db, currentUser.UserId, today, cancellationToken))
                    .Where(c => c.NextExpected >= today && c.NextExpected <= today.AddDays(35))
                    .OrderBy(c => c.NextExpected)
                    .Take(6)
                    .Select(c => new
                    {
                        name = c.DisplayName,
                        expectedOn = c.NextExpected.ToString("yyyy-MM-dd"),
                        amount = c.AverageAmount,
                    });

                // ── Recent activity ──
                var recent = (await db.Transactions
                        .OrderByDescending(t => t.OccurredOn).ThenByDescending(t => t.CreatedAt)
                        .Take(60)
                        .ToListAsync(cancellationToken))
                    .Where(t => visibleIds.Contains(t.AccountId))
                    .Take(8)
                    .Select(t => new
                    {
                        occurredOn = t.OccurredOn.ToString("yyyy-MM-dd"),
                        description = t.Description,
                        amount = t.Amount,
                        currencyCode = t.CurrencyCode,
                        direction = t.Direction,
                    });

                // ── Goal progress (same math as the goals tab; private-account goals stay null) ──
                var accountsById = accounts.ToDictionary(a => a.Id);
                var goals = (await db.Goals.OrderBy(g => g.Name).Take(5).ToListAsync(cancellationToken))
                    .Select(g =>
                    {
                        var saved = GoalTools.GoalProgress(g, accountsById);
                        return new
                        {
                            name = g.Name,
                            saved,
                            target = g.TargetAmount,
                            currencyCode = g.CurrencyCode,
                        };
                    });

                return Results.Ok(new
                {
                    asOf = today.ToString("yyyy-MM-dd"),
                    currencyCode,
                    safeToSpend = safeToSpend is null
                        ? null
                        : new
                        {
                            amount = safeToSpend.Amount,
                            currencyCode,
                            month = period.ToString("yyyy-MM"),
                            budgetCount = safeToSpend.BudgetCount,
                            totalTarget = safeToSpend.TotalTarget,
                            totalSpent = safeToSpend.TotalSpent,
                        },
                    netWorth = new { total = netWorthTotal, currencyCode, trend },
                    budgets = budgetRows.Take(6),
                    upcomingBills = upcoming,
                    recentTransactions = recent,
                    goals,
                });
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(FinanceModule.ViewFinance))
            .WithName("Finance_Overview");
    }
}

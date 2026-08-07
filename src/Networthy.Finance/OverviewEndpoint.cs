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
/// nowhere else. Deliberately conservative, deterministic, and — the property issue #149 was
/// filed about — REPRODUCIBLE from the totals shipped beside it: a reader holding only the
/// payload can always recompute the number as max(0, totalTarget − totalSpent). No budgets at
/// all means there is no honest number to show, so the answer is null rather than a fabrication
/// (the UI renders guidance instead).
/// </summary>
public static class SafeToSpendMath
{
    public sealed record SafeToSpend(decimal Amount, decimal TotalTarget, decimal TotalSpent, int BudgetCount);

    /// <summary>
    /// max(0, Σtarget − Σspent) over the month's budgets; null when there are none.
    /// <para>
    /// This clamps once, on the aggregate. It previously clamped per category — Σ max(0, target −
    /// spent) — which discarded real overspend and so overstated headroom: two budgets at 400/100
    /// and 100/300 read 300 while shipping totalTarget 500 and totalSpent 400 (issue #149). That
    /// was not conservative, it was optimistic, and it contradicted the very totals beside it.
    /// Overspend is money already gone, so it reduces the figure; the aggregate clamp still keeps
    /// the result from ever going negative, which was the only property the per-category clamp
    /// was actually needed for.
    /// </para>
    /// </summary>
    public static SafeToSpend? Compute(IReadOnlyList<(decimal Target, decimal Spent)> budgets)
    {
        if (budgets.Count == 0) return null;

        var totalTarget = budgets.Sum(b => b.Target);
        var totalSpent = budgets.Sum(b => b.Spent);

        return new SafeToSpend(
            Math.Max(0m, totalTarget - totalSpent),
            totalTarget,
            totalSpent,
            budgets.Count);
    }
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
                        .Where(t => t.Direction == "expense" && t.TransferGroupId == null
                                    && t.OccurredOn >= period && t.OccurredOn <= monthEnd)
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

                // ── Net worth: live total from visible accounts, combined across currencies with
                // the SAME NetWorthMath.Combine the get_net_worth tool calls — one implementation,
                // so the dashboard and the assistant cannot answer this question differently.
                // Issue #173: this used to sum only the accounts already denominated in the
                // household default and silently discard the rest, so a household holding 1,000 USD
                // and 2,000 EUR at a saved 1.1 read "1,000" here while the assistant said 3,200.
                // A currency with no saved rate is still excluded — the household's own rates or
                // nothing, never a guessed one — but it now ships in `excluded` so the screen can
                // say what was left out. Tenant-wide snapshots stay admin-only because their
                // aggregates can include another member's private account. ──
                var fxRates = (await db.ExchangeRates.ToListAsync(cancellationToken))
                    .ToDictionary(r => r.CurrencyCode, r => r.RateToDefault, StringComparer.OrdinalIgnoreCase);
                var (netWorthTotal, netWorthConverted, netWorthExcluded) = NetWorthMath.Combine(
                    NetWorthMath.SumByCurrency(accounts), currencyCode, fxRates);
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
                    netWorth = new
                    {
                        total = netWorthTotal,
                        currencyCode,
                        trend,
                        // Both lists make the total auditable from the payload alone — the same
                        // property SafeToSpendMath was fixed to honour in #149: a reader holding
                        // only this response can reconstruct where every figure came from.
                        converted = netWorthConverted.Select(c => new
                        {
                            currencyCode = c.Currency,
                            amount = c.Original,
                            convertedAmount = c.Converted,
                            rateToDefault = fxRates[c.Currency],
                        }),
                        excluded = netWorthExcluded.Select(e => new
                        {
                            currencyCode = e.Currency,
                            amount = e.Total,
                        }),
                    },
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

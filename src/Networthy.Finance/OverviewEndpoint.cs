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
    /// <param name="Amount">
    /// max(0, TotalTarget − TotalSpent), or null when budgets exist but NONE of them could be
    /// expressed in the household currency. Null is "no honest number", never "zero left".
    /// </param>
    public sealed record SafeToSpend(
        decimal? Amount,
        decimal TotalTarget,
        decimal TotalSpent,
        int BudgetCount,
        IReadOnlyList<ConvertedBudgets> Converted,
        IReadOnlyList<ExcludedBudgets> Excluded);

    /// <summary>Budgets in a foreign currency folded in through the household's own saved rate.</summary>
    public sealed record ConvertedBudgets(
        string CurrencyCode, decimal Target, decimal Spent,
        decimal ConvertedTarget, decimal ConvertedSpent, decimal RateToDefault, int BudgetCount);

    /// <summary>Budgets left OUT because the household saved no rate for that currency.</summary>
    public sealed record ExcludedBudgets(string CurrencyCode, decimal Target, decimal Spent, int BudgetCount);

    /// <summary>
    /// max(0, Σtarget − Σspent) over the month's budgets, in the household currency; null when
    /// there are no budgets at all.
    /// <para>
    /// This clamps once, on the aggregate. It previously clamped per category — Σ max(0, target −
    /// spent) — which discarded real overspend and so overstated headroom: two budgets at 400/100
    /// and 100/300 read 300 while shipping totalTarget 500 and totalSpent 400 (issue #149). That
    /// was not conservative, it was optimistic, and it contradicted the very totals beside it.
    /// Overspend is money already gone, so it reduces the figure; the aggregate clamp still keeps
    /// the result from ever going negative, which was the only property the per-category clamp
    /// was actually needed for.
    /// </para>
    /// <para>
    /// Issue #214: the caller used to filter the list down to the household's default currency
    /// before calling in, so a budget in any other currency vanished — and with a single foreign
    /// budget the result was a bare null, which the Overview tab renders as "No budgets yet this
    /// month" directly beside a card listing that budget. Two read surfaces, one screen, flat
    /// contradiction. Currency is therefore handled HERE, under the same policy
    /// <see cref="NetWorthMath.Combine"/> already committed to for net worth in #173: the
    /// household's own saved rate converts, a currency with no saved rate is never guessed at —
    /// but it is returned in <see cref="SafeToSpend.Excluded"/> so the screen can say what it left
    /// out. The distinction the UI needs is now in the payload: <c>null</c> means there are no
    /// budgets, while a record whose <c>Amount</c> is null means there are budgets that cannot be
    /// combined, and names them.
    /// </para>
    /// </summary>
    public static SafeToSpend? Compute(
        IReadOnlyList<(decimal Target, decimal Spent, string CurrencyCode)> budgets,
        string defaultCurrency,
        IReadOnlyDictionary<string, decimal> ratesToDefault)
    {
        if (budgets.Count == 0) return null;

        var totalTarget = 0m;
        var totalSpent = 0m;
        var budgetCount = 0;
        var converted = new List<ConvertedBudgets>();
        var excluded = new List<ExcludedBudgets>();

        // Grouped per currency so the disclosure lists each currency once, the way netWorth's
        // converted/excluded lists do, rather than once per budget row.
        foreach (var group in budgets
                     .GroupBy(b => b.CurrencyCode.ToUpperInvariant(), StringComparer.Ordinal)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var target = group.Sum(b => b.Target);
            var spent = group.Sum(b => b.Spent);
            var count = group.Count();

            if (string.Equals(group.Key, defaultCurrency, StringComparison.OrdinalIgnoreCase))
            {
                totalTarget += target;
                totalSpent += spent;
                budgetCount += count;
            }
            else if (ratesToDefault.TryGetValue(group.Key, out var rate))
            {
                // Rounded per currency, exactly as NetWorthMath.Combine rounds per currency, so the
                // shipped convertedTarget/convertedSpent still add up to the totals beside them.
                var convertedTarget = Math.Round(target * rate, 2);
                var convertedSpent = Math.Round(spent * rate, 2);
                totalTarget += convertedTarget;
                totalSpent += convertedSpent;
                budgetCount += count;
                converted.Add(new ConvertedBudgets(
                    group.Key, target, spent, convertedTarget, convertedSpent, rate, count));
            }
            else
            {
                excluded.Add(new ExcludedBudgets(group.Key, target, spent, count));
            }
        }

        return new SafeToSpend(
            budgetCount == 0 ? null : Math.Max(0m, totalTarget - totalSpent),
            totalTarget,
            totalSpent,
            budgetCount,
            converted,
            excluded);
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

                // The household's own exchange rates, read once and used by BOTH currency-scoped
                // figures below — safe-to-spend (#214) and net worth (#173). One dictionary, so the
                // two cards on the screen can never disagree about what a currency is worth.
                var fxRates = (await db.ExchangeRates.ToListAsync(cancellationToken))
                    .ToDictionary(r => r.CurrencyCode, r => r.RateToDefault, StringComparer.OrdinalIgnoreCase);

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
                // Issue #214: every budget goes in, whatever currency it is in. Filtering the list to
                // the household default here is what produced a null safe-to-spend beside a
                // populated `budgets` array — the contradiction the issue was filed about. The
                // currency policy belongs to SafeToSpendMath, which converts through the saved rates
                // and reports whatever it could not convert.
                var safeToSpend = SafeToSpendMath.Compute(
                    budgetRows.Select(b => (b.target, b.spent, b.currencyCode)).ToList(),
                    currencyCode,
                    fxRates);

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
                    // Null ONLY when the household has no budgets this month — the one state in
                    // which "No budgets yet this month" is a true sentence. When budgets exist but
                    // none could be expressed in the household currency, this is an object whose
                    // `amount` is null and whose `excluded` names them, so the screen states the
                    // reason instead of asserting an emptiness the card beside it contradicts (#214).
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
                            // Both lists keep the figure reproducible from the payload alone —
                            // #149's property, now covering the FX step as well.
                            converted = safeToSpend.Converted.Select(c => new
                            {
                                currencyCode = c.CurrencyCode,
                                target = c.Target,
                                spent = c.Spent,
                                convertedTarget = c.ConvertedTarget,
                                convertedSpent = c.ConvertedSpent,
                                rateToDefault = c.RateToDefault,
                                budgetCount = c.BudgetCount,
                            }),
                            excluded = safeToSpend.Excluded.Select(e => new
                            {
                                currencyCode = e.CurrencyCode,
                                target = e.Target,
                                spent = e.Spent,
                                budgetCount = e.BudgetCount,
                            }),
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

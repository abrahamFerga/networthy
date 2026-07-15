using Plenipo.Application.Authorization;
using Plenipo.Core.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Networthy.Finance.Persistence;

namespace Networthy.Finance;

/// <summary>
/// Pure cash-flow bucketing, unit-tested without a database: transactions land in one
/// (month, direction) bucket each, and every month in the window emits BOTH directions —
/// zero-filled — so the chart's x-axis never skips a quiet month and the income/expense
/// pairing stays visible even when one side is empty. A window with NO activity at all
/// yields no rows (the chart renders its honest empty state, not twelve silent months).
/// </summary>
public static class CashFlowMath
{
    public sealed record MonthFlow(string Month, string Direction, decimal Amount);

    /// <summary>
    /// Income and expense totals per month over the <paramref name="months"/>-month window
    /// ending at <paramref name="today"/>'s month, oldest first, income before expense within
    /// each month. Rows outside the window or with an unknown direction are ignored.
    /// </summary>
    public static IReadOnlyList<MonthFlow> MonthlyTotals(
        IEnumerable<(DateOnly OccurredOn, string Direction, decimal Amount)> transactions,
        DateOnly today, int months = 12)
    {
        var currentMonth = new DateOnly(today.Year, today.Month, 1);
        var windowStart = currentMonth.AddMonths(-(months - 1));
        var windowEnd = currentMonth.AddMonths(1).AddDays(-1);
        var totals = new Dictionary<(string Month, string Direction), decimal>();
        foreach (var (occurredOn, direction, amount) in transactions)
        {
            if (occurredOn < windowStart || occurredOn > windowEnd ||
                direction is not ("income" or "expense"))
            {
                continue;
            }

            totals[(occurredOn.ToString("yyyy-MM"), direction)] =
                totals.GetValueOrDefault((occurredOn.ToString("yyyy-MM"), direction)) + amount;
        }

        if (totals.Count == 0)
        {
            return [];
        }

        var rows = new List<MonthFlow>(months * 2);
        for (var i = months - 1; i >= 0; i--)
        {
            var month = currentMonth.AddMonths(-i).ToString("yyyy-MM");
            rows.Add(new MonthFlow(month, "income", totals.GetValueOrDefault((month, "income"))));
            rows.Add(new MonthFlow(month, "expense", totals.GetValueOrDefault((month, "expense"))));
        }

        return rows;
    }
}

/// <summary>
/// The Cash flow tab's read (issue #46): money in vs money out per month, last 12 months. Rows
/// are shaped for the shell's grouped-bar chart — one bar per direction per month, amounts
/// absolute (the direction field carries the sign's meaning). Scoped like every composed read:
/// caller-visible accounts only, and only the household currency, so the two bars are always
/// the same unit and never a cross-currency fiction.
/// </summary>
internal static class CashFlowEndpoint
{
    internal static void MapCashFlowEndpoint(this IEndpointRouteBuilder group)
    {
        ((RouteGroupBuilder)group).MapGet("/cashflow", async (
                FinanceDbContext db, ICurrentUser currentUser, HouseholdContext household,
                CancellationToken cancellationToken) =>
            {
                var today = await household.TodayAsync(cancellationToken);
                var currencyCode = await household.ResolveCurrencyAsync(null, cancellationToken);
                var windowStart = new DateOnly(today.Year, today.Month, 1).AddMonths(-11);

                var visibleIds = (await db.Accounts.ToListAsync(cancellationToken))
                    .Where(a => a.IsVisibleTo(currentUser.UserId))
                    .Select(a => a.Id)
                    .ToHashSet();
                var rows = (await db.Transactions
                        .Where(t => t.OccurredOn >= windowStart)
                        .ToListAsync(cancellationToken))
                    .Where(t => visibleIds.Contains(t.AccountId) &&
                                t.CurrencyCode.Equals(currencyCode, StringComparison.OrdinalIgnoreCase))
                    .Select(t => (t.OccurredOn, t.Direction, t.Amount));

                return Results.Ok(CashFlowMath.MonthlyTotals(rows, today)
                    .Select(f => new { month = f.Month, direction = f.Direction, amount = f.Amount }));
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(FinanceModule.ViewFinance))
            .WithName("Finance_CashFlow");
    }
}

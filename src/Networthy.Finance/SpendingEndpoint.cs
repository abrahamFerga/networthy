using Cortex.Application.Authorization;
using Cortex.Core.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Networthy.Finance.Persistence;

namespace Networthy.Finance;

/// <summary>
/// The Spending tab's read (issue #46): one month's expenses summed per category — the same
/// <see cref="SpendingMath.SummarizeByCategory"/> the chat assistant's summarize_spending runs,
/// so the donut can never disagree with the answer in chat. The month defaults to the
/// household's current month; networthy-ui's SpendingTab re-points the query at any of the
/// last 12 via <c>?month=yyyy-MM</c>. Household-currency scoped: a category's segment is one
/// currency's total, never a cross-currency sum.
/// </summary>
internal static class SpendingEndpoint
{
    internal static void MapSpendingEndpoint(this IEndpointRouteBuilder group)
    {
        ((RouteGroupBuilder)group).MapGet("/spending", async (
                string? month, FinanceDbContext db, ICurrentUser currentUser, HouseholdContext household,
                CancellationToken cancellationToken) =>
            {
                var today = await household.TodayAsync(cancellationToken);
                if (!BudgetMath.TryParseMonth(month, today, out var period))
                {
                    return Results.BadRequest(new { error = $"'{month}' is not a month — use yyyy-MM, e.g. {today:yyyy-MM}." });
                }

                var monthEnd = period.AddMonths(1).AddDays(-1);
                var currencyCode = await household.ResolveCurrencyAsync(null, cancellationToken);
                var visibleIds = (await db.Accounts.ToListAsync(cancellationToken))
                    .Where(a => a.IsVisibleTo(currentUser.UserId))
                    .Select(a => a.Id)
                    .ToHashSet();
                var expenses = (await db.Transactions
                        .Where(t => t.Direction == "expense" && t.OccurredOn >= period && t.OccurredOn <= monthEnd)
                        .ToListAsync(cancellationToken))
                    .Where(t => visibleIds.Contains(t.AccountId) &&
                                t.CurrencyCode.Equals(currencyCode, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var categoryNames = await db.Categories.ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);

                return Results.Ok(SpendingMath.SummarizeByCategory(expenses, categoryNames)
                    .Select(l => new { category = l.Category, amount = l.Total, currencyCode = l.Currency, count = l.Count }));
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(FinanceModule.ViewFinance))
            .WithName("Finance_Spending");
    }
}

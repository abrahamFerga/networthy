using Cortex.Application.Authorization;
using Cortex.Core.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Networthy.Finance.Persistence;

namespace Networthy.Finance;

/// <summary>
/// Pure calendar projection over detected recurring charges, unit-tested without a database.
/// Detection stays <see cref="RecurringDetection"/>'s alone — this only repeats each charge's
/// next expected date forward by its cadence so a 60-day calendar shows a monthly bill twice
/// and a weekly one every week, not just once. A charge detection dated before today (a late
/// or just-missed bill) surfaces at its next on-rhythm date instead of vanishing for a cycle.
/// </summary>
public static class BillsCalendarMath
{
    public sealed record BillOccurrence(string Name, DateOnly DueOn, decimal Amount);

    /// <summary>Every expected occurrence inside [<paramref name="today"/>, <paramref name="until"/>], soonest first.</summary>
    public static IReadOnlyList<BillOccurrence> Project(
        IEnumerable<RecurringCharge> charges, DateOnly today, DateOnly until)
    {
        var occurrences = new List<BillOccurrence>();
        foreach (var charge in charges)
        {
            var due = charge.NextExpected;
            while (due < today)
            {
                due = Step(due, charge.Cadence);
            }

            for (; due <= until; due = Step(due, charge.Cadence))
            {
                occurrences.Add(new BillOccurrence(charge.DisplayName, due, charge.AverageAmount));
            }
        }

        return [.. occurrences.OrderBy(o => o.DueOn).ThenBy(o => o.Name)];
    }

    private static DateOnly Step(DateOnly from, string cadence) => cadence switch
    {
        "weekly" => from.AddDays(7),
        "biweekly" => from.AddDays(14),
        "monthly" => from.AddMonths(1),
        "quarterly" => from.AddMonths(3),
        "yearly" => from.AddYears(1),
        // ClassifyGap emits nothing else today; monthly is the conservative guess if it ever does.
        _ => from.AddMonths(1),
    };
}

/// <summary>
/// The Recurring tab calendar's read (issue #46): the same detection the tab's table and the
/// Overview's "upcoming bills" run (<see cref="RecurringTools.DetectAsync"/>), projected over
/// the next 60 days. Composed (not a bare array) because the calendar needs the household's
/// own "today" to mark the right cell — the browser's clock may sit in a different day.
/// Amounts render in the household currency, same as the Overview's bills list.
/// </summary>
internal static class UpcomingBillsEndpoint
{
    internal static void MapUpcomingBillsEndpoint(this IEndpointRouteBuilder group)
    {
        ((RouteGroupBuilder)group).MapGet("/bills/upcoming", async (
                FinanceDbContext db, ICurrentUser currentUser, HouseholdContext household,
                CancellationToken cancellationToken) =>
            {
                var today = await household.TodayAsync(cancellationToken);
                var until = today.AddDays(60);
                var currencyCode = await household.ResolveCurrencyAsync(null, cancellationToken);
                var charges = await RecurringTools.DetectAsync(db, currentUser.UserId, today, cancellationToken);

                return Results.Ok(new
                {
                    today = today.ToString("yyyy-MM-dd"),
                    until = until.ToString("yyyy-MM-dd"),
                    currencyCode,
                    bills = BillsCalendarMath.Project(charges, today, until)
                        .Select(b => new { name = b.Name, dueOn = b.DueOn.ToString("yyyy-MM-dd"), amount = b.Amount }),
                });
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(FinanceModule.ViewFinance))
            .WithName("Finance_UpcomingBills");
    }
}

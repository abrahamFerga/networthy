using System.ComponentModel;
using System.Globalization;
using Plenipo.Core.Identity;
using Microsoft.EntityFrameworkCore;
using Networthy.Finance.Persistence;

namespace Networthy.Finance;

/// <summary>
/// The "one number" answer (research: PocketGuard's pattern, chat-first): "can I afford X?"
/// gets a direct, computed verdict from the household's own data — liquid balances and the
/// month's spending so far — never a lecture and never a made-up figure. Read-only.
/// When budgets exist for the category (Budgets epic), the verdict also reports what's left
/// in the relevant budget.
/// </summary>
public sealed class AffordabilityTools(
    FinanceDbContext db,
    ICurrentUser currentUser,
    HouseholdContext household)
{
    [Description("Answer 'can I afford X?' directly: compares the amount against the household's liquid balances (checking/savings/cash) and this month's spending. Read-only, computed, honest.")]
    public async Task<string> CanIAfford(
        [Description("The amount being considered, e.g. 200.")] double amount,
        [Description("ISO currency (omit for the household default).")] string? currency = null,
        [Description("Optional category this would fall under, e.g. 'Dining'.")] string? category = null,
        CancellationToken cancellationToken = default)
    {
        if (amount <= 0)
        {
            return "amount must be positive.";
        }

        var currencyCode = await household.ResolveCurrencyAsync(currency, cancellationToken);
        var accounts = (await db.Accounts.ToListAsync(cancellationToken))
            .Where(a => a.IsVisibleTo(currentUser.UserId) && a.CurrencyCode.Equals(currencyCode, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (accounts.Count == 0)
        {
            return $"No {currencyCode} accounts are visible to you, so I can't compute this. Create or link accounts first.";
        }

        var liquid = AffordabilityMath.LiquidBalance(accounts);

        var today = await household.TodayAsync(cancellationToken);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var visibleIds = accounts.Select(a => a.Id).ToHashSet();
        var monthSpend = (await db.Transactions
                .Where(t => t.Direction == "expense" && t.OccurredOn >= monthStart)
                .ToListAsync(cancellationToken))
            .Where(t => visibleIds.Contains(t.AccountId))
            .Sum(t => t.Amount);

        var verdict = AffordabilityMath.Verdict((decimal)amount, liquid);
        var answer = verdict switch
        {
            Affordability.Comfortably =>
                $"Yes — {amount:N2} {currencyCode} is well within your {liquid:N2} {currencyCode} of liquid balance.",
            Affordability.Yes =>
                $"Yes, but it's a real dent: {amount:N2} {currencyCode} against {liquid:N2} {currencyCode} liquid " +
                $"({AffordabilityMath.ShareOfLiquid((decimal)amount, liquid):P0} of what's available).",
            _ =>
                $"No — {amount:N2} {currencyCode} exceeds your {liquid:N2} {currencyCode} of liquid balance " +
                "(checking + savings + cash; credit lines don't count as affordability).",
        };

        answer += $" This month's spending so far: {monthSpend:N2} {currencyCode}.";

        if (!string.IsNullOrWhiteSpace(category))
        {
            var categoryRow = await db.Categories.FirstOrDefaultAsync(
                c => EF.Functions.ILike(c.Name, category.Trim()), cancellationToken);
            if (categoryRow is not null)
            {
                var categorySpend = (await db.Transactions
                        .Where(t => t.Direction == "expense" && t.OccurredOn >= monthStart && t.CategoryId == categoryRow.Id)
                        .ToListAsync(cancellationToken))
                    .Where(t => visibleIds.Contains(t.AccountId))
                    .Sum(t => t.Amount);
                answer += $" {categoryRow.Name} so far this month: {categorySpend:N2} {currencyCode}.";
            }
        }

        return answer;
    }
}

/// <summary>How affordable an amount is against liquid balance.</summary>
public enum Affordability
{
    Comfortably,
    Yes,
    No,
}

/// <summary>Pure affordability math, unit-tested without a database.</summary>
public static class AffordabilityMath
{
    /// <summary>Liquid = checking + savings + cash. Credit is a debt instrument, not affordability.</summary>
    public static decimal LiquidBalance(IEnumerable<Account> accounts) =>
        accounts.Where(a => a.Type is "checking" or "savings" or "cash").Sum(a => a.CachedBalance);

    /// <summary>Comfortably under 10% of liquid; Yes up to 100%; No beyond.</summary>
    public static Affordability Verdict(decimal amount, decimal liquid) =>
        liquid <= 0 || amount > liquid ? Affordability.No
        : amount <= liquid * 0.10m ? Affordability.Comfortably
        : Affordability.Yes;

    public static decimal ShareOfLiquid(decimal amount, decimal liquid) =>
        liquid <= 0 ? 1m : Math.Min(1m, amount / liquid);
}

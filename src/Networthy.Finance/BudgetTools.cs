using System.ComponentModel;
using System.Globalization;
using System.Text;
using Cortex.Core.Identity;
using Cortex.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Networthy.Finance.Persistence;

namespace Networthy.Finance;

/// <summary>
/// Budgets (SPEC must-have #5): per-category monthly targets, spent-vs-target from approved
/// transactions at read time, and honest over-budget flags. Setting a target is a record change
/// and approval-gated; reading status never is.
/// </summary>
public sealed class BudgetTools(
    FinanceDbContext db,
    ITenantContext tenant,
    ICurrentUser currentUser,
    HouseholdContext household)
{
    [Description("Set (or change) a category's monthly budget target, e.g. 'set the grocery budget to $400'. Side-effecting and requires approval.")]
    public async Task<string> SetBudget(
        [Description("The category name (must exist — see the Categories tab).")] string category,
        [Description("The monthly target amount, positive.")] double amount,
        [Description("ISO currency (omit for the household default).")] string? currency = null,
        [Description("The month as yyyy-MM (default: the current month).")] string? month = null,
        CancellationToken cancellationToken = default)
    {
        if (amount <= 0)
        {
            return "amount must be positive.";
        }

        var categoryRow = await db.Categories.FirstOrDefaultAsync(
            c => EF.Functions.ILike(c.Name, category.Trim()), cancellationToken);
        if (categoryRow is null)
        {
            return $"No category named '{category}' exists. Check the Categories tab first.";
        }

        if (!BudgetMath.TryParseMonth(month, await household.TodayAsync(cancellationToken), out var period))
        {
            return $"'{month}' is not a month I can parse — use yyyy-MM, e.g. 2026-07.";
        }

        var currencyCode = await household.ResolveCurrencyAsync(currency, cancellationToken);
        var existing = await db.Budgets.FirstOrDefaultAsync(
            b => b.CategoryId == categoryRow.Id && b.PeriodMonth == period && b.CurrencyCode == currencyCode,
            cancellationToken);
        if (existing is null)
        {
            db.Budgets.Add(new Budget
            {
                TenantId = tenant.RequireTenantId(),
                CategoryId = categoryRow.Id,
                PeriodMonth = period,
                TargetAmount = (decimal)amount,
                CurrencyCode = currencyCode,
            });
        }
        else
        {
            existing.TargetAmount = (decimal)amount;
        }

        await db.SaveChangesAsync(cancellationToken);
        return $"Budget for {categoryRow.Name} in {period:yyyy-MM}: {amount:N2} {currencyCode}.";
    }

    [Description("Budget status for a month: spent vs target per category, with over-budget flags. Defaults to the current month.")]
    public async Task<string> GetBudgetStatus(
        [Description("The month as yyyy-MM (default: the current month).")] string? month = null,
        CancellationToken cancellationToken = default)
    {
        if (!BudgetMath.TryParseMonth(month, await household.TodayAsync(cancellationToken), out var period))
        {
            return $"'{month}' is not a month I can parse — use yyyy-MM, e.g. 2026-07.";
        }

        var budgets = await db.Budgets.Where(b => b.PeriodMonth == period).ToListAsync(cancellationToken);
        if (budgets.Count == 0)
        {
            return $"No budgets set for {period:yyyy-MM}. Set one with set_budget (category, amount).";
        }

        var monthEnd = period.AddMonths(1).AddDays(-1);
        var visibleAccountIds = (await db.Accounts.ToListAsync(cancellationToken))
            .Where(a => a.IsVisibleTo(currentUser.UserId))
            .Select(a => a.Id)
            .ToHashSet();
        var spentRows = (await db.Transactions
                .Where(t => t.Direction == "expense" && t.OccurredOn >= period && t.OccurredOn <= monthEnd)
                .ToListAsync(cancellationToken))
            .Where(t => visibleAccountIds.Contains(t.AccountId))
            .ToList();
        var categories = await db.Categories.ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);

        var sb = new StringBuilder($"Budgets for {period:yyyy-MM}:\n");
        foreach (var budget in budgets.OrderBy(b => categories.GetValueOrDefault(b.CategoryId, "?")))
        {
            var spent = spentRows
                .Where(t => t.CategoryId == budget.CategoryId &&
                            t.CurrencyCode.Equals(budget.CurrencyCode, StringComparison.OrdinalIgnoreCase))
                .Sum(t => t.Amount);
            var status = BudgetMath.Status(budget.TargetAmount, spent);
            var name = categories.GetValueOrDefault(budget.CategoryId, "(deleted category)");
            sb.AppendLine(
                $"- {name}: {spent:N2} / {budget.TargetAmount:N2} {budget.CurrencyCode}" +
                (status.Over
                    ? $" — ⚠ OVER by {-status.Remaining:N2}"
                    : $" — {status.Remaining:N2} left"));
        }

        return sb.ToString();
    }
}

/// <summary>Pure budget math, unit-tested without a database.</summary>
public static class BudgetMath
{
    public sealed record BudgetStatus(decimal Remaining, bool Over);

    public static BudgetStatus Status(decimal target, decimal spent) =>
        new(target - spent, spent > target);

    /// <summary>Parses "yyyy-MM" (null/empty = the current month, UTC). Prefer the today-aware overload.</summary>
    public static bool TryParseMonth(string? month, out DateOnly period) =>
        TryParseMonth(month, DateOnly.FromDateTime(DateTime.UtcNow), out period);

    /// <summary>Parses "yyyy-MM" (null/empty = <paramref name="today"/>'s month) to the month's first day.</summary>
    public static bool TryParseMonth(string? month, DateOnly today, out DateOnly period)
    {
        if (string.IsNullOrWhiteSpace(month))
        {
            period = new DateOnly(today.Year, today.Month, 1);
            return true;
        }

        if (DateOnly.TryParseExact($"{month.Trim()}-01", "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out period))
        {
            return true;
        }

        period = default;
        return false;
    }

    /// <summary>Which of last month's budgets to copy into a month that has none yet — the pure
    /// core of the rollover job. Keys are (CategoryId, CurrencyCode).</summary>
    public static IReadOnlyList<Budget> RolloverPlan(
        IReadOnlyList<Budget> previousMonth, IReadOnlySet<(Guid, string)> alreadyPresent) =>
        [.. previousMonth.Where(b => !alreadyPresent.Contains((b.CategoryId, b.CurrencyCode)))];
}

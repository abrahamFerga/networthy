using System.ComponentModel;
using System.Globalization;
using System.Text;
using Cortex.Core.Identity;
using Cortex.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Networthy.Finance.Persistence;

namespace Networthy.Finance;

/// <summary>
/// Savings goals (SPEC differentiator): targets with computed, honest progress. Writes are
/// approval-gated like every record change (ADR-0002); reads answer "am I on track?" with the
/// household's own numbers, never encouragement-flavored guesses.
/// </summary>
public sealed class GoalTools(
    FinanceDbContext db,
    ITenantContext tenant,
    ICurrentUser currentUser)
{
    [Description("Create or update a savings goal ('save $5,000 for Hawaii by June'). Optionally link an account whose balance IS the progress. Side-effecting and requires approval.")]
    public async Task<string> SetGoal(
        [Description("The goal's name, e.g. 'Hawaii trip' or 'Emergency fund'.")] string name,
        [Description("The target amount, positive.")] double targetAmount,
        [Description("ISO currency (default USD).")] string currency = "USD",
        [Description("Optional deadline as an ISO date, e.g. 2027-06-01.")] string? targetDate = null,
        [Description("Optional account name whose balance tracks this goal (e.g. a dedicated savings account).")] string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return "A goal needs a name.";
        }

        if (targetAmount <= 0)
        {
            return "targetAmount must be positive.";
        }

        DateOnly? deadline = null;
        if (!string.IsNullOrWhiteSpace(targetDate))
        {
            if (!DateOnly.TryParse(targetDate, CultureInfo.InvariantCulture, out var parsed))
            {
                return $"'{targetDate}' is not a date I can parse — use an ISO date like 2027-06-01.";
            }

            deadline = parsed;
        }

        Guid? accountId = null;
        if (!string.IsNullOrWhiteSpace(accountName))
        {
            var account = await db.Accounts.FirstOrDefaultAsync(
                a => EF.Functions.ILike(a.Name, accountName.Trim()), cancellationToken);
            if (account is null || !account.IsVisibleTo(currentUser.UserId))
            {
                return $"No account named '{accountName}' exists (or it is private to another member). Use list_accounts.";
            }

            accountId = account.Id;
        }

        var currencyCode = currency.Trim().ToUpperInvariant();
        var existing = await db.Goals.FirstOrDefaultAsync(
            g => EF.Functions.ILike(g.Name, trimmed), cancellationToken);
        if (existing is null)
        {
            db.Goals.Add(new Goal
            {
                TenantId = tenant.RequireTenantId(),
                Name = trimmed,
                TargetAmount = (decimal)targetAmount,
                CurrencyCode = currencyCode,
                TargetDate = deadline,
                AccountId = accountId,
                CreatedByUserId = currentUser.UserId,
            });
            await db.SaveChangesAsync(cancellationToken);
            return $"Goal '{trimmed}': {targetAmount:N2} {currencyCode}" +
                   $"{(deadline is { } d ? $" by {d:yyyy-MM-dd}" : "")}" +
                   $"{(accountId is null ? " (progress by contributions — contribute_to_goal)" : $" (tracked by '{accountName!.Trim()}')")}.";
        }

        existing.TargetAmount = (decimal)targetAmount;
        existing.CurrencyCode = currencyCode;
        existing.TargetDate = deadline;
        existing.AccountId = accountId;
        await db.SaveChangesAsync(cancellationToken);
        return $"Updated goal '{existing.Name}': {targetAmount:N2} {currencyCode}" +
               $"{(deadline is { } nd ? $" by {nd:yyyy-MM-dd}" : "")}.";
    }

    [Description("Record progress toward an unlinked goal ('put $300 toward Hawaii'). A bookkeeping marker, not a transaction — balances don't move. Side-effecting and requires approval.")]
    public async Task<string> ContributeToGoal(
        [Description("The goal's name.")] string name,
        [Description("The amount to add, positive (negative corrections allowed).")] double amount,
        CancellationToken cancellationToken = default)
    {
        var goal = await db.Goals.FirstOrDefaultAsync(
            g => EF.Functions.ILike(g.Name, name.Trim()), cancellationToken);
        if (goal is null)
        {
            return $"No goal named '{name}' exists. Use list_goals, or create it with set_goal.";
        }

        if (goal.AccountId is not null)
        {
            return $"'{goal.Name}' is tracked by a linked account — its balance IS the progress. " +
                   "Move money into that account instead of recording a contribution.";
        }

        if (amount == 0)
        {
            return "amount must be non-zero.";
        }

        goal.SavedAmount += (decimal)amount;
        if (goal.SavedAmount < 0)
        {
            goal.SavedAmount = 0;
        }

        await db.SaveChangesAsync(cancellationToken);
        return $"'{goal.Name}': {goal.SavedAmount:N2} / {goal.TargetAmount:N2} {goal.CurrencyCode} " +
               $"({GoalMath.Percent(goal.SavedAmount, goal.TargetAmount):P0}).";
    }

    [Description("The household's savings goals with computed progress and an on-pace verdict for dated goals.")]
    public async Task<string> ListGoals(CancellationToken cancellationToken = default)
    {
        var goals = await db.Goals.OrderBy(g => g.Name).ToListAsync(cancellationToken);
        if (goals.Count == 0)
        {
            return "No goals yet. Create one with set_goal ('save $5,000 for Hawaii by June').";
        }

        var accounts = (await db.Accounts.ToListAsync(cancellationToken))
            .Where(a => a.IsVisibleTo(currentUser.UserId))
            .ToDictionary(a => a.Id);
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);

        var sb = new StringBuilder("Goals:\n");
        foreach (var goal in goals)
        {
            var saved = GoalProgress(goal, accounts);
            if (saved is null)
            {
                sb.AppendLine($"- {goal.Name}: tracked by an account that is private to another member.");
                continue;
            }

            sb.Append($"- {goal.Name}: {saved:N2} / {goal.TargetAmount:N2} {goal.CurrencyCode} " +
                      $"({GoalMath.Percent(saved.Value, goal.TargetAmount):P0})");
            if (goal.TargetDate is { } deadline)
            {
                sb.Append($", target {deadline:yyyy-MM-dd} — " +
                          (GoalMath.OnPace(saved.Value, goal.TargetAmount, goal.CreatedAt, deadline, today)
                              ? "on pace"
                              : "behind pace"));
            }

            sb.AppendLine(goal.AccountId is not null ? " [account-linked]" : "");
        }

        return sb.ToString();
    }

    /// <summary>Null when the linked account is invisible to the caller (never leak its balance).</summary>
    internal static decimal? GoalProgress(Goal goal, IReadOnlyDictionary<Guid, Account> visibleAccounts) =>
        goal.AccountId is { } id
            ? visibleAccounts.TryGetValue(id, out var account) ? account.CachedBalance : null
            : goal.SavedAmount;
}

/// <summary>Pure goal math, unit-tested without a database.</summary>
public static class GoalMath
{
    public static decimal Percent(decimal saved, decimal target) =>
        target <= 0 ? 0 : Math.Clamp(saved / target, 0, 1);

    /// <summary>On pace = actual progress ≥ the straight-line share of elapsed time.</summary>
    public static bool OnPace(decimal saved, decimal target, DateTimeOffset createdAt, DateOnly deadline, DateOnly today)
    {
        var start = DateOnly.FromDateTime(createdAt.UtcDateTime);
        var totalDays = deadline.DayNumber - start.DayNumber;
        if (totalDays <= 0 || today >= deadline)
        {
            return saved >= target;
        }

        var elapsed = Math.Max(0, today.DayNumber - start.DayNumber);
        var expected = target * elapsed / totalDays;
        return saved >= expected;
    }
}

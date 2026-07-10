using System.ComponentModel;
using System.Text;
using Cortex.Core.Identity;
using Cortex.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Networthy.Finance.Persistence;

namespace Networthy.Finance;

/// <summary>
/// Answers "HOW do I reach this goal?": the required contribution per month AND per paycheck
/// (from the household's declared income cadences), a where-to-save suggestion drawn from their
/// own accounts, and an honest cash-flow fit check. For invested goals the future-value annuity
/// math uses the household's OWN assumed return — the tool never invents one, states the
/// assumption in the output, and shows the 0%-growth contribution beside it so the assumption's
/// weight is visible. Standard calculator math on their numbers; never investment advice.
/// </summary>
public sealed class GoalPlanTools(
    FinanceDbContext db,
    ICurrentUser currentUser,
    HouseholdContext household)
{
    [Description("The plan for reaching a goal: required contribution per month and per paycheck, where to put the money (from your own accounts), and whether it fits current cash flow. For invested goals, uses the goal's assumed annual return and shows the no-growth figure beside it. Read-only; math, not advice.")]
    public async Task<string> GetGoalPlan(
        [Description("The goal's name.")] string name,
        CancellationToken cancellationToken = default)
    {
        var goal = await db.Goals.FirstOrDefaultAsync(
            g => EF.Functions.ILike(g.Name, name.Trim()), cancellationToken);
        if (goal is null)
        {
            return $"No goal named '{name}' exists. Use list_goals, or create it with set_goal.";
        }

        if (goal.TargetDate is not { } deadline)
        {
            return $"'{goal.Name}' has no target date, so there is no schedule to plan against. " +
                   "Give it one with set_goal (targetDate) — 'by age 55' works too: tell me the year that lands on.";
        }

        var today = await household.TodayAsync(cancellationToken);
        var months = GoalPlanMath.MonthsBetween(today, deadline);
        if (months <= 0)
        {
            return $"'{goal.Name}' has a target date in the past ({deadline:yyyy-MM-dd}) — move it with set_goal first.";
        }

        var accounts = (await db.Accounts.ToListAsync(cancellationToken))
            .Where(a => a.IsVisibleTo(currentUser.UserId))
            .ToList();
        var saved = GoalTools.GoalProgress(goal, accounts.ToDictionary(a => a.Id)) ?? 0m;
        var remaining = Math.Max(0, goal.TargetAmount - saved);

        var sb = new StringBuilder(
            $"Plan for '{goal.Name}' — {goal.TargetAmount:N2} {goal.CurrencyCode} by {deadline:yyyy-MM-dd} ({months} month(s) away):\n");
        sb.AppendLine($"- Saved so far: {saved:N2} ({GoalMath.Percent(saved, goal.TargetAmount):P0})");

        if (remaining == 0)
        {
            sb.Append("Already there — nothing left to contribute.");
            return sb.ToString();
        }

        var flat = GoalPlanMath.RequiredMonthlyContribution(goal.TargetAmount, saved, months, annualReturnPct: 0);
        decimal required;
        if (goal.ExpectedAnnualReturnPct is { } assumedReturn and > 0)
        {
            required = GoalPlanMath.RequiredMonthlyContribution(goal.TargetAmount, saved, months, assumedReturn);
            sb.AppendLine($"- Assumed annual return: {assumedReturn:0.##}% — YOUR assumption, not a promise; actual returns vary and can be negative.");
            sb.AppendLine($"- Required contribution at that assumption: ≈{required:N2}/month");
            sb.AppendLine($"- Without growth (0%): {flat:N2}/month — the gap is what the return assumption is doing.");
        }
        else
        {
            required = flat;
            sb.AppendLine($"- Required contribution: {required:N2}/month");
        }

        // Per-paycheck framing from the household's declared income cadences.
        var incomeSources = await db.IncomeSources.OrderBy(i => i.Name).ToListAsync(cancellationToken);
        foreach (var source in incomeSources.Where(i =>
                     i.CurrencyCode.Equals(goal.CurrencyCode, StringComparison.OrdinalIgnoreCase)))
        {
            var perPaycheck = GoalPlanMath.PerPaycheck(required, source.Cadence);
            sb.AppendLine($"  · ≈{perPaycheck:N2} per {source.Cadence} paycheck ('{source.Name}')");
        }

        // Honest fit check: declared income minus recent actual spending.
        var expectedMonthlyIncome = incomeSources
            .Where(i => i.CurrencyCode.Equals(goal.CurrencyCode, StringComparison.OrdinalIgnoreCase))
            .Sum(i => i.MonthlyEquivalent);
        if (expectedMonthlyIncome > 0)
        {
            var since = today.AddDays(-90);
            var visibleIds = accounts.Select(a => a.Id).ToHashSet();
            var expense90 = (await db.Transactions
                    .Where(t => t.Direction == "expense" && t.OccurredOn >= since)
                    .ToListAsync(cancellationToken))
                .Where(t => visibleIds.Contains(t.AccountId) &&
                            t.CurrencyCode.Equals(goal.CurrencyCode, StringComparison.OrdinalIgnoreCase))
                .Sum(t => t.Amount);
            var avgMonthlyExpenses = expense90 / 3m;
            var capacity = expectedMonthlyIncome - avgMonthlyExpenses;
            sb.AppendLine(capacity >= required
                ? $"- Fits your cash flow: declared income {expectedMonthlyIncome:N2}/mo − avg expenses {avgMonthlyExpenses:N2}/mo leaves {capacity:N2}/mo ({(capacity == 0 ? 0 : required / capacity):P0} of it goes to this goal)."
                : $"- Does NOT fit current cash flow: declared income {expectedMonthlyIncome:N2}/mo − avg expenses {avgMonthlyExpenses:N2}/mo leaves {capacity:N2}/mo, {required - capacity:N2} short — a later date, a smaller target, or spending changes close the gap.");
        }

        // Where: from their own accounts, never a product pitch.
        if (goal.AccountId is null)
        {
            var savingsNames = accounts.Where(a => a.Type == "savings").Select(a => $"'{a.Name}'").ToList();
            sb.AppendLine(goal.ExpectedAnnualReturnPct is > 0
                ? "- Where: an invested goal's money lives at your brokerage — track contributions here with contribute_to_goal so the plan stays honest."
                : savingsNames.Count > 0
                    ? $"- Where: link a savings account so the balance IS the progress (set_goal accountName) — you have {string.Join(", ", savingsNames)}."
                    : "- Where: a dedicated savings account makes progress automatic — create one (create_account) and link it with set_goal.");
        }

        sb.Append("Math on your own numbers and your own assumptions — not financial advice.");
        return sb.ToString();
    }
}

/// <summary>Pure goal-planning math, unit-tested without a database.</summary>
public static class GoalPlanMath
{
    /// <summary>Whole months from <paramref name="from"/> to <paramref name="to"/> (floor, min 0).</summary>
    public static int MonthsBetween(DateOnly from, DateOnly to)
    {
        var months = (to.Year - from.Year) * 12 + to.Month - from.Month;
        if (to.Day < from.Day)
        {
            months--;
        }

        return Math.Max(0, months);
    }

    /// <summary>
    /// The monthly contribution that reaches <paramref name="target"/> in <paramref name="months"/>,
    /// starting from <paramref name="saved"/>. With a return assumption, standard future-value
    /// annuity math at nominal-annual/12 compounding: the current balance grows too, and
    /// PMT = (FV − saved·(1+i)^n) · i / ((1+i)^n − 1).
    /// </summary>
    public static decimal RequiredMonthlyContribution(decimal target, decimal saved, int months, decimal annualReturnPct)
    {
        if (months <= 0)
        {
            return Math.Max(0, target - saved);
        }

        if (annualReturnPct <= 0)
        {
            return Math.Max(0, Math.Round((target - saved) / months, 2));
        }

        var i = (double)annualReturnPct / 100d / 12d;
        var growth = Math.Pow(1 + i, months);
        var futureOfSaved = (double)saved * growth;
        var payment = ((double)target - futureOfSaved) * i / (growth - 1);
        return Math.Max(0, Math.Round((decimal)payment, 2));
    }

    /// <summary>A monthly amount restated per paycheck for a cadence (26 biweekly pays a year, not 24).</summary>
    public static decimal PerPaycheck(decimal monthly, string cadence) =>
        Math.Round(monthly * 12m / IncomeSource.PaychecksPerYear(cadence), 2);
}

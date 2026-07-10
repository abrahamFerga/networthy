using System.ComponentModel;
using System.Text;
using Cortex.Core.Identity;
using Microsoft.EntityFrameworkCore;
using Networthy.Finance.Persistence;

namespace Networthy.Finance;

/// <summary>
/// The financial-health assessment: every number computed from the household's own data, every
/// suggestion a STANDARD money-management strategy (debt avalanche, emergency-fund coverage,
/// savings rate) triggered by an explicit threshold — never investment advice, never a made-up
/// score. The AI narrates this tool's output; it does not improvise financial guidance.
/// </summary>
public sealed class HealthTools(
    FinanceDbContext db,
    ICurrentUser currentUser)
{
    [Description("A computed financial-health check: net worth, what debts cost per month, emergency-fund coverage, savings rate, budget status — plus standard, data-triggered ways to improve. Read-only; not investment advice.")]
    public async Task<string> GetFinancialHealth(
        [Description("ISO currency to assess (default USD; other currencies are reported separately).")] string currency = "USD",
        CancellationToken cancellationToken = default)
    {
        var currencyCode = currency.Trim().ToUpperInvariant();
        var accounts = (await db.Accounts.ToListAsync(cancellationToken))
            .Where(a => a.IsVisibleTo(currentUser.UserId) &&
                        a.CurrencyCode.Equals(currencyCode, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (accounts.Count == 0)
        {
            return $"No {currencyCode} accounts are visible to you — there is nothing to assess yet. " +
                   "Create accounts (create_account) and log or import some activity first.";
        }

        // Cash flow over the last 90 days, from the household's own transactions.
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        var since = today.AddDays(-90);
        var visibleIds = accounts.Select(a => a.Id).ToHashSet();
        var recent = (await db.Transactions
                .Where(t => t.OccurredOn >= since)
                .ToListAsync(cancellationToken))
            .Where(t => visibleIds.Contains(t.AccountId) &&
                        t.CurrencyCode.Equals(currencyCode, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var income90 = recent.Where(t => t.Direction == "income").Sum(t => t.Amount);
        var expense90 = recent.Where(t => t.Direction == "expense").Sum(t => t.Amount);

        // This month's budget flags ride the existing budget math.
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var budgets = await db.Budgets.Where(b => b.PeriodMonth == monthStart).ToListAsync(cancellationToken);
        var monthExpenses = (await db.Transactions
                .Where(t => t.Direction == "expense" && t.OccurredOn >= monthStart)
                .ToListAsync(cancellationToken))
            .Where(t => visibleIds.Contains(t.AccountId))
            .ToList();
        var overBudget = budgets
            .Where(b => BudgetMath.Status(
                b.TargetAmount,
                monthExpenses.Where(t => t.CategoryId == b.CategoryId &&
                                         t.CurrencyCode.Equals(b.CurrencyCode, StringComparison.OrdinalIgnoreCase))
                    .Sum(t => t.Amount)).Over)
            .Count();

        var snapshot = FinancialHealthMath.Assess(accounts, income90, expense90, budgets.Count, overBudget);

        var sb = new StringBuilder($"Financial health ({currencyCode}, computed from your own data):\n");
        sb.AppendLine($"- Net worth: {snapshot.NetWorth:N2}");
        sb.AppendLine($"- Liquid (checking+savings+cash): {snapshot.Liquid:N2}");

        if (snapshot.Debts.Count > 0)
        {
            sb.AppendLine($"- Debt: {snapshot.TotalDebt:N2} across {snapshot.Debts.Count} account(s)" +
                          (snapshot.MonthlyInterestCost > 0
                              ? $", costing ≈{snapshot.MonthlyInterestCost:N2}/month in interest" +
                                $" (weighted avg {snapshot.WeightedApr:0.##}% APR)"
                              : " (no interest rates recorded — update_account_terms to price it)"));
            foreach (var d in snapshot.Debts)
            {
                sb.AppendLine($"  · {d.Name}: {d.Owed:N2} owed" +
                              (d.Apr is { } apr ? $" @ {apr:0.###}% ≈{d.MonthlyInterest:N2}/mo interest" : " (APR unknown)"));
            }
        }
        else
        {
            sb.AppendLine("- Debt: none recorded.");
        }

        sb.AppendLine($"- Last 90 days: income {income90:N2}, expenses {expense90:N2}" +
                      (snapshot.SavingsRate is { } rate ? $", savings rate {rate:P0}" : ""));
        sb.AppendLine(snapshot.EmergencyFundMonths is { } months
            ? $"- Emergency fund: liquid covers ≈{months:0.#} month(s) of average expenses."
            : "- Emergency fund: no expense history yet to measure coverage against.");
        if (budgets.Count > 0)
        {
            sb.AppendLine($"- Budgets this month: {budgets.Count} set, {overBudget} over.");
        }

        sb.AppendLine();
        if (snapshot.Suggestions.Count == 0)
        {
            sb.Append("No standard flags raised — the fundamentals look steady on the recorded data.");
        }
        else
        {
            sb.AppendLine("Ways to improve (standard strategies triggered by your own numbers — not financial advice):");
            for (var i = 0; i < snapshot.Suggestions.Count; i++)
            {
                sb.AppendLine($"{i + 1}. {snapshot.Suggestions[i]}");
            }
        }

        return sb.ToString();
    }
}

/// <summary>One debt account's contribution to the health picture.</summary>
public sealed record DebtLine(string Name, decimal Owed, decimal? Apr, decimal MonthlyInterest);

/// <summary>The computed health snapshot.</summary>
public sealed record HealthSnapshot(
    decimal NetWorth,
    decimal Liquid,
    IReadOnlyList<DebtLine> Debts,
    decimal TotalDebt,
    decimal WeightedApr,
    decimal MonthlyInterestCost,
    decimal? SavingsRate,
    decimal? EmergencyFundMonths,
    IReadOnlyList<string> Suggestions);

/// <summary>Pure financial-health math and the suggestion thresholds, unit-tested without a database.</summary>
public static class FinancialHealthMath
{
    /// <summary>APR at/above this is "high-interest" (typical card territory vs. typical loans).</summary>
    public const decimal HighAprThreshold = 8m;

    /// <summary>Standard guidance floor for emergency-fund coverage.</summary>
    public const decimal EmergencyFundFloorMonths = 3m;

    /// <summary>Savings-rate floor below which the assessment flags cash flow.</summary>
    public const decimal SavingsRateFloor = 0.10m;

    public static HealthSnapshot Assess(
        IReadOnlyList<Account> accounts, decimal income90, decimal expense90, int budgetCount, int overBudgetCount)
    {
        var netWorth = accounts.Sum(a => a.CachedBalance);
        var liquid = AffordabilityMath.LiquidBalance(accounts);

        var debts = accounts
            .Where(a => a.IsDebt && a.CachedBalance < 0)
            .Select(a => new DebtLine(
                a.Name,
                Math.Abs(a.CachedBalance),
                a.InterestRateApr,
                a.InterestRateApr is { } apr ? Math.Round(Math.Abs(a.CachedBalance) * apr / 100m / 12m, 2) : 0m))
            .OrderByDescending(d => d.Apr ?? -1) // avalanche order: costliest first
            .ToList();
        var totalDebt = debts.Sum(d => d.Owed);
        var pricedDebt = debts.Where(d => d.Apr is not null).Sum(d => d.Owed);
        var weightedApr = pricedDebt > 0
            ? debts.Where(d => d.Apr is not null).Sum(d => d.Owed * d.Apr!.Value) / pricedDebt
            : 0m;
        var monthlyInterest = debts.Sum(d => d.MonthlyInterest);

        decimal? savingsRate = income90 > 0 ? (income90 - expense90) / income90 : null;
        var avgMonthlyExpenses = expense90 / 3m;
        decimal? emergencyMonths = avgMonthlyExpenses > 0 ? liquid / avgMonthlyExpenses : null;

        return new HealthSnapshot(
            netWorth, liquid, debts, totalDebt, weightedApr, monthlyInterest, savingsRate, emergencyMonths,
            BuildSuggestions(debts, liquid, savingsRate, emergencyMonths, budgetCount, overBudgetCount));
    }

    /// <summary>
    /// Deterministic, threshold-triggered suggestions. Each is a standard strategy stated with
    /// the household's own numbers — the list is empty when nothing is flagged, never padded.
    /// </summary>
    internal static IReadOnlyList<string> BuildSuggestions(
        IReadOnlyList<DebtLine> debts, decimal liquid, decimal? savingsRate, decimal? emergencyMonths,
        int budgetCount, int overBudgetCount)
    {
        var suggestions = new List<string>();

        var highInterest = debts.Where(d => d.Apr >= HighAprThreshold).ToList();
        if (highInterest.Count > 0)
        {
            var costliest = highInterest[0]; // avalanche order already
            suggestions.Add(
                $"High-interest debt first (avalanche): '{costliest.Name}' at {costliest.Apr:0.##}% costs " +
                $"≈{costliest.MonthlyInterest:N2}/month — extra payments there save the most interest. " +
                (debts.Count > 1 ? "Alternative: the snowball method (smallest balance first) trades some interest for quicker wins." : ""));
        }

        var unpriced = debts.Where(d => d.Apr is null).ToList();
        if (unpriced.Count > 0)
        {
            suggestions.Add(
                $"Record the interest rate on {string.Join(", ", unpriced.Select(d => $"'{d.Name}'"))} " +
                "(update_account_terms) — unpriced debt hides its real monthly cost.");
        }

        if (emergencyMonths is { } months && months < EmergencyFundFloorMonths)
        {
            // Standard caveat: past ~1 month of buffer, high-APR paydown usually beats idle cash —
            // only push the fund when there is no high-interest debt eating faster than savings earn.
            suggestions.Add(highInterest.Count == 0
                ? $"Emergency fund covers ≈{months:0.#} month(s) of expenses; the common guideline is {EmergencyFundFloorMonths}–6 months in liquid accounts."
                : $"Emergency fund covers ≈{months:0.#} month(s); common practice is a small buffer first, then high-interest paydown, then building toward {EmergencyFundFloorMonths}–6 months.");
        }

        if (savingsRate is { } rate && rate < SavingsRateFloor)
        {
            suggestions.Add(rate < 0
                ? "Spending exceeded income over the last 90 days — summarize_spending shows where; budgets (set_budget) put guardrails on the biggest categories."
                : $"Savings rate is {rate:P0} over the last 90 days; raising it toward 10–20% is the usual first lever, and budgets on the top spending categories are the usual tool.");
        }

        if (overBudgetCount > 0)
        {
            suggestions.Add($"{overBudgetCount} budget(s) are over this month — get_budget_status names them; adjusting the target or the spending are both honest fixes.");
        }

        if (budgetCount == 0 && debts.Count + (liquid > 0 ? 1 : 0) > 0)
        {
            suggestions.Add("No budgets are set for this month — even two or three on the biggest categories make overspending visible early (set_budget).");
        }

        return suggestions;
    }
}

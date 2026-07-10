using System.ComponentModel;
using System.Text;
using Cortex.Core.Identity;
using Cortex.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Networthy.Finance.Persistence;

namespace Networthy.Finance;

/// <summary>
/// Declared income schedules — the cadence layer under cash-flow capacity and per-paycheck goal
/// planning. Declaring a schedule changes no balances (paychecks still arrive as transactions);
/// it records what the household EXPECTS, so writes are approval-gated like every record change.
/// </summary>
public sealed class IncomeSourceTools(
    FinanceDbContext db,
    ITenantContext tenant,
    ICurrentUser currentUser,
    HouseholdContext household)
{
    [Description("Declare or update a recurring income ('I get 2,500 every two weeks from ACME'). Cadences: weekly, biweekly, semimonthly (twice a month), monthly. Side-effecting and requires approval.")]
    public async Task<string> SetIncomeSource(
        [Description("A name for the income, e.g. 'ACME payroll'.")] string name,
        [Description("The amount received EACH TIME (per paycheck, not per month).")] double amount,
        [Description("weekly, biweekly (every two weeks), semimonthly (twice a month), or monthly.")] string cadence,
        [Description("ISO currency (omit for the household default).")] string? currency = null,
        [Description("Optional account name the income lands in.")] string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0 || amount <= 0)
        {
            return "An income source needs a name and a positive per-paycheck amount.";
        }

        var normalizedCadence = IncomeSource.NormalizeCadence(cadence);
        if (normalizedCadence is null)
        {
            return $"'{cadence}' is not a cadence I know. Use weekly, biweekly, semimonthly, or monthly.";
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

        var currencyCode = await household.ResolveCurrencyAsync(currency, cancellationToken);
        var existing = await db.IncomeSources.FirstOrDefaultAsync(
            i => EF.Functions.ILike(i.Name, trimmed), cancellationToken);
        if (existing is null)
        {
            existing = new IncomeSource
            {
                TenantId = tenant.RequireTenantId(),
                Name = trimmed,
                Amount = (decimal)amount,
                CurrencyCode = currencyCode,
                Cadence = normalizedCadence,
                AccountId = accountId,
                CreatedByUserId = currentUser.UserId,
            };
            db.IncomeSources.Add(existing);
        }
        else
        {
            existing.Amount = (decimal)amount;
            existing.CurrencyCode = currencyCode;
            existing.Cadence = normalizedCadence;
            existing.AccountId = accountId;
        }

        await db.SaveChangesAsync(cancellationToken);
        return $"Income '{existing.Name}': {existing.Amount:N2} {existing.CurrencyCode} {existing.Cadence} " +
               $"(≈{existing.MonthlyEquivalent:N2}/month).";
    }

    [Description("The household's declared income schedules with their monthly equivalents.")]
    public async Task<string> ListIncomeSources(CancellationToken cancellationToken = default)
    {
        var sources = await db.IncomeSources.OrderBy(i => i.Name).ToListAsync(cancellationToken);
        if (sources.Count == 0)
        {
            return "No income schedules declared yet. Declare one with set_income_source " +
                   "('I get 2,500 every two weeks from ACME').";
        }

        var sb = new StringBuilder("Income schedules:\n");
        foreach (var source in sources)
        {
            sb.AppendLine($"- {source.Name}: {source.Amount:N2} {source.CurrencyCode} {source.Cadence} " +
                          $"(≈{source.MonthlyEquivalent:N2}/month)");
        }

        foreach (var group in sources.GroupBy(s => s.CurrencyCode))
        {
            sb.AppendLine($"Expected total: ≈{group.Sum(s => s.MonthlyEquivalent):N2} {group.Key}/month");
        }

        return sb.ToString();
    }
}

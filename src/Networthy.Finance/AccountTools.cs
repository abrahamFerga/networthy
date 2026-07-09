using System.ComponentModel;
using System.Text;
using Cortex.Core.Identity;
using Cortex.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Networthy.Finance.Persistence;

namespace Networthy.Finance;

/// <summary>
/// Account management and the net-worth roll-up. Creating an account is a record change and
/// approval-gated like every other write (ADR-0002); listings honor per-member visibility
/// scoping — a restricted account is invisible to other members, not just hidden.
/// </summary>
public sealed class AccountTools(
    FinanceDbContext db,
    ITenantContext tenant,
    ICurrentUser currentUser)
{
    [Description("Create a financial account (checking, savings, credit, or cash) for the household. Side-effecting and requires approval.")]
    public async Task<string> CreateAccount(
        [Description("The account's display name, e.g. 'Chase Checking' or 'Groceries cash'.")] string name,
        [Description("The account type: checking, savings, credit, or cash.")] string type,
        [Description("ISO currency code, e.g. USD. Ask the user if unsure — never guess.")] string currency,
        [Description("The opening balance (negative for credit owed). Default 0.")] double openingBalance = 0,
        [Description("Optional institution name, e.g. 'Chase'.")] string? institution = null,
        [Description("Optional masked number for display, e.g. '4321' (last digits only).")] string? lastDigits = null,
        CancellationToken cancellationToken = default)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return "An account needs a name.";
        }

        var normalizedType = Account.NormalizeType(type);
        if (normalizedType is null)
        {
            return $"'{type}' is not an account type. Use checking, savings, credit, or cash.";
        }

        var currencyCode = currency.Trim().ToUpperInvariant();
        if (currencyCode.Length != 3)
        {
            return $"'{currency}' is not an ISO currency code (e.g. USD, MXN, EUR).";
        }

        var existing = await db.Accounts.FirstOrDefaultAsync(
            a => EF.Functions.ILike(a.Name, trimmed), cancellationToken);
        if (existing is not null)
        {
            return $"An account named '{existing.Name}' already exists. Use it, or pick a different name.";
        }

        var account = new Account
        {
            TenantId = tenant.RequireTenantId(),
            Name = trimmed,
            Type = normalizedType,
            CurrencyCode = currencyCode,
            CachedBalance = (decimal)openingBalance,
            InstitutionName = string.IsNullOrWhiteSpace(institution) ? null : institution.Trim(),
            MaskedAccountNumber = string.IsNullOrWhiteSpace(lastDigits) ? null : $"••••{lastDigits.Trim()}",
            CreatedByUserId = currentUser.UserId,
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync(cancellationToken);

        return $"Created {normalizedType} account '{account.Name}' ({currencyCode}) " +
               $"with opening balance {account.CachedBalance:N2}.";
    }

    [Description("List the household's accounts with their types and current balances (accounts restricted to another member are not shown).")]
    public async Task<string> ListAccounts(CancellationToken cancellationToken = default)
    {
        var accounts = (await db.Accounts.OrderBy(a => a.Name).Take(100).ToListAsync(cancellationToken))
            .Where(a => a.IsVisibleTo(currentUser.UserId))
            .ToList();
        if (accounts.Count == 0)
        {
            return "No accounts yet. Create one with create_account (name, type, currency).";
        }

        var sb = new StringBuilder("Accounts:\n");
        foreach (var a in accounts)
        {
            sb.AppendLine($"- {a.Name} [{a.Type}] {a.CachedBalance:N2} {a.CurrencyCode}" +
                          $"{(a.InstitutionName is null ? "" : $" — {a.InstitutionName}")}" +
                          $"{(a.MaskedAccountNumber is null ? "" : $" {a.MaskedAccountNumber}")}" +
                          $"{(a.RestrictedToUserId is null ? "" : " (private)")}");
        }

        return sb.ToString();
    }

    [Description("The household's net worth: every visible account summed per currency, with the recent trend when snapshots exist.")]
    public async Task<string> GetNetWorth(CancellationToken cancellationToken = default)
    {
        var accounts = (await db.Accounts.ToListAsync(cancellationToken))
            .Where(a => a.IsVisibleTo(currentUser.UserId))
            .ToList();
        if (accounts.Count == 0)
        {
            return "No accounts yet, so no net worth to report. Create accounts first (create_account).";
        }

        var totals = NetWorthMath.SumByCurrency(accounts);
        var sb = new StringBuilder("Net worth:\n");
        foreach (var (currency, total) in totals)
        {
            sb.AppendLine($"- {total:N2} {currency}");
        }

        var since = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime).AddDays(-30);
        var snapshots = await db.NetWorthSnapshots
            .Where(s => s.TakenOn >= since)
            .OrderBy(s => s.TakenOn)
            .ToListAsync(cancellationToken);
        if (snapshots.Count > 0)
        {
            foreach (var group in snapshots.GroupBy(s => s.CurrencyCode))
            {
                var first = group.First();
                var last = group.Last();
                var delta = last.NetWorth - first.NetWorth;
                sb.AppendLine(
                    $"Trend ({group.Key}, since {first.TakenOn:yyyy-MM-dd}): " +
                    $"{(delta >= 0 ? "+" : "")}{delta:N2}");
            }
        }

        return sb.ToString();
    }
}

/// <summary>Pure net-worth math, unit-tested without a database.</summary>
public static class NetWorthMath
{
    /// <summary>Sums balances per currency. Credit balances are stored negative when owed, so a
    /// straight sum IS assets minus liabilities.</summary>
    public static IReadOnlyList<(string Currency, decimal Total)> SumByCurrency(IEnumerable<Account> accounts) =>
        [.. accounts
            .GroupBy(a => a.CurrencyCode, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Currency: g.Key.ToUpperInvariant(), Total: g.Sum(a => a.CachedBalance)))
            .OrderBy(t => t.Currency, StringComparer.Ordinal)];
}

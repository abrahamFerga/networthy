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
    ICurrentUser currentUser,
    HouseholdContext household)
{
    [Description("Create a financial account (checking, savings, credit, cash, or loan — mortgage/auto/student/personal) for the household. Side-effecting and requires approval.")]
    public async Task<string> CreateAccount(
        [Description("The account's display name, e.g. 'Chase Checking' or 'House mortgage'.")] string name,
        [Description("The account type: checking, savings, credit, cash, or loan.")] string type,
        [Description("ISO currency code, e.g. USD. Omit to use the household default; never guess a DIFFERENT currency.")] string? currency = null,
        [Description("The opening balance (negative for money owed on credit/loan). Default 0.")] double openingBalance = 0,
        [Description("Optional institution name, e.g. 'Chase'.")] string? institution = null,
        [Description("Optional masked number for display, e.g. '4321' (last digits only).")] string? lastDigits = null,
        [Description("Annual interest rate as a percent for credit/loan accounts, e.g. 6.25. Ask — never guess.")] double? interestRateApr = null,
        [Description("Optional contractual minimum monthly payment for credit/loan accounts.")] double? minimumMonthlyPayment = null,
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

        var currencyCode = await household.ResolveCurrencyAsync(currency, cancellationToken);
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

        if (interestRateApr is < 0 or > 100)
        {
            return $"{interestRateApr} is not an APR I can accept — use the annual percentage, e.g. 6.25.";
        }

        // A loan entered as a positive figure ("I owe 250,000") is money owed — store it negative
        // so net worth stays a straight sum. Explicitly-negative input is already correct.
        var balance = (decimal)openingBalance;
        if (normalizedType is "loan" && balance > 0)
        {
            balance = -balance;
        }

        var account = new Account
        {
            TenantId = tenant.RequireTenantId(),
            Name = trimmed,
            Type = normalizedType,
            CurrencyCode = currencyCode,
            CachedBalance = balance,
            InstitutionName = string.IsNullOrWhiteSpace(institution) ? null : institution.Trim(),
            MaskedAccountNumber = string.IsNullOrWhiteSpace(lastDigits) ? null : $"••••{lastDigits.Trim()}",
            InterestRateApr = (decimal?)interestRateApr,
            MinimumMonthlyPayment = (decimal?)minimumMonthlyPayment,
            CreatedByUserId = currentUser.UserId,
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync(cancellationToken);

        return $"Created {normalizedType} account '{account.Name}' ({currencyCode}) " +
               $"with opening balance {account.CachedBalance:N2}" +
               $"{(account.InterestRateApr is { } apr ? $" at {apr:0.###}% APR" : "")}" +
               $"{(account.MinimumMonthlyPayment is { } min ? $", minimum payment {min:N2}" : "")}.";
    }

    [Description("Set or correct a debt account's terms: interest rate (APR %) and/or minimum monthly payment. Side-effecting and requires approval.")]
    public async Task<string> UpdateAccountTerms(
        [Description("The account's name.")] string accountName,
        [Description("Annual interest rate as a percent, e.g. 21.99. Omit to leave unchanged.")] double? interestRateApr = null,
        [Description("Contractual minimum monthly payment. Omit to leave unchanged; 0 clears it.")] double? minimumMonthlyPayment = null,
        CancellationToken cancellationToken = default)
    {
        var account = await db.Accounts.FirstOrDefaultAsync(
            a => EF.Functions.ILike(a.Name, accountName.Trim()), cancellationToken);
        if (account is null || !account.IsVisibleTo(currentUser.UserId))
        {
            return $"No account named '{accountName}' exists (or it is private to another member). Use list_accounts.";
        }

        if (interestRateApr is null && minimumMonthlyPayment is null)
        {
            return "Nothing to change — pass interestRateApr and/or minimumMonthlyPayment.";
        }

        if (interestRateApr is < 0 or > 100)
        {
            return $"{interestRateApr} is not an APR I can accept — use the annual percentage, e.g. 21.99.";
        }

        if (interestRateApr is { } newApr)
        {
            account.InterestRateApr = (decimal)newApr;
        }

        if (minimumMonthlyPayment is { } newMin)
        {
            account.MinimumMonthlyPayment = newMin == 0 ? null : (decimal)newMin;
        }

        await db.SaveChangesAsync(cancellationToken);
        return $"'{account.Name}' terms: " +
               $"{(account.InterestRateApr is { } apr ? $"{apr:0.###}% APR" : "APR unknown")}" +
               $"{(account.MinimumMonthlyPayment is { } min ? $", minimum payment {min:N2} {account.CurrencyCode}" : "")}.";
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
                          $"{(a.InterestRateApr is { } apr ? $" @ {apr:0.###}% APR" : "")}" +
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

        // Combine across currencies when the household saved rates: the default currency as the
        // measuring stick, every rate user-chosen and shown. Currencies without a rate are never
        // silently guessed — they are listed as unconverted with the tool that fixes it.
        if (totals.Count > 1)
        {
            var defaultCurrency = (await household.GetSettingsAsync(cancellationToken)).DefaultCurrencyCode;
            var fxRates = (await db.ExchangeRates.ToListAsync(cancellationToken))
                .ToDictionary(r => r.CurrencyCode, r => r.RateToDefault, StringComparer.OrdinalIgnoreCase);
            var (combined, converted, unconvertible) = NetWorthMath.Combine(totals, defaultCurrency, fxRates);
            if (converted.Count > 0)
            {
                sb.AppendLine(
                    $"Combined: {combined:N2} {defaultCurrency} (your saved rates: " +
                    string.Join(", ", converted.Select(c => $"1 {c.Currency} = {fxRates[c.Currency]:0.####} {defaultCurrency}")) + ")");
            }

            foreach (var (unconvertedCurrency, unconvertedTotal) in unconvertible)
            {
                sb.AppendLine(
                    $"Not combined: {unconvertedTotal:N2} {unconvertedCurrency} — no saved rate " +
                    $"(set_exchange_rate {unconvertedCurrency} to include it).");
            }
        }

        var since = (await household.TodayAsync(cancellationToken)).AddDays(-30);
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

    /// <summary>
    /// Combines per-currency totals into the default currency using the household's saved rates
    /// (units of default per 1 unit of foreign). The default itself counts at 1; a currency with
    /// no saved rate is returned separately — never guessed at.
    /// </summary>
    public static (decimal Total,
        IReadOnlyList<(string Currency, decimal Original, decimal Converted)> Converted,
        IReadOnlyList<(string Currency, decimal Total)> Unconvertible)
        Combine(
            IReadOnlyList<(string Currency, decimal Total)> totals,
            string defaultCurrency,
            IReadOnlyDictionary<string, decimal> ratesToDefault)
    {
        var combined = 0m;
        var converted = new List<(string, decimal, decimal)>();
        var unconvertible = new List<(string, decimal)>();
        foreach (var (currency, total) in totals)
        {
            if (string.Equals(currency, defaultCurrency, StringComparison.OrdinalIgnoreCase))
            {
                combined += total;
            }
            else if (ratesToDefault.TryGetValue(currency, out var rate))
            {
                var value = Math.Round(total * rate, 2);
                combined += value;
                converted.Add((currency, total, value));
            }
            else
            {
                unconvertible.Add((currency, total));
            }
        }

        return (combined, converted, unconvertible);
    }
}

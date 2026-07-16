using Plenipo.Core.Entities;

namespace Networthy.Finance.Persistence;

/// <summary>
/// A financial account the household tracks: checking, savings, credit card, cash, or a loan
/// (mortgage, auto, student, personal — anything owed with terms). Multi-currency from the data
/// model up (SPEC: US-first UX, but every amount carries its currency).
/// <see cref="CachedBalance"/> starts from the opening balance and moves with each posted
/// transaction; DEBT balances (credit, loan) are stored negative when owed, so net worth is a
/// straight sum. <see cref="RestrictedToUserId"/> is the per-member visibility scope: null means
/// household-wide; a user id means only that member (and household admins) see it.
/// </summary>
public sealed class Account : TenantEntityBase
{
    public required string Name { get; set; }

    /// <summary>checking | savings | credit | cash | loan.</summary>
    public required string Type { get; set; }

    /// <summary>Annual interest rate as a percentage (e.g. 6.25 = 6.25% APR). Debt accounts
    /// only; what get_financial_health prices the household's debt with. Null = unknown.</summary>
    public decimal? InterestRateApr { get; set; }

    /// <summary>The contractual minimum monthly payment, when the household tracks it.</summary>
    public decimal? MinimumMonthlyPayment { get; set; }

    /// <summary>Where the account is held (PII — flows through export/audit tagging).</summary>
    public string? InstitutionName { get; set; }

    /// <summary>Display-only masked number, e.g. "••••4321" (PII). Never a full account number.</summary>
    public string? MaskedAccountNumber { get; set; }

    /// <summary>ISO 4217, e.g. "USD".</summary>
    public required string CurrencyCode { get; set; }

    /// <summary>Current balance; negative for credit owed. Moves with posted transactions.</summary>
    public decimal CachedBalance { get; set; }

    /// <summary>Null = visible to the whole household; else only this member + household admins.</summary>
    public Guid? RestrictedToUserId { get; set; }

    public Guid? CreatedByUserId { get; set; }

    /// <summary>Normalizes free-text account types to the five the module speaks.</summary>
    public static string? NormalizeType(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        "checking" or "cheque" or "current" => "checking",
        "savings" or "saving" => "savings",
        "credit" or "credit card" or "creditcard" or "card" => "credit",
        "cash" or "wallet" => "cash",
        "loan" or "mortgage" or "auto loan" or "car loan" or "student loan" or "personal loan" or "heloc" => "loan",
        _ => null,
    };

    /// <summary>Debt instruments: balances owed, priced by <see cref="InterestRateApr"/>.</summary>
    public bool IsDebt => Type is "credit" or "loan";

    /// <summary>True when <paramref name="userId"/> may see this account (admins bypass via permissions).</summary>
    public bool IsVisibleTo(Guid? userId) => RestrictedToUserId is null || RestrictedToUserId == userId;
}

/// <summary>
/// A daily point-in-time record of the household's net worth per currency — the data behind the
/// trend view. Written by the snapshot service; append-only.
/// </summary>
public sealed class NetWorthSnapshot : TenantEntityBase
{
    public DateOnly TakenOn { get; set; }

    /// <summary>ISO 4217 — one snapshot row per currency per day.</summary>
    public required string CurrencyCode { get; set; }

    public decimal NetWorth { get; set; }
}

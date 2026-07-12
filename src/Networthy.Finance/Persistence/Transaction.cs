using Cortex.Core.Entities;

namespace Networthy.Finance.Persistence;

/// <summary>
/// One movement of money on an account. Income and expense are the same entity with a
/// <see cref="Direction"/> (SPEC: income is a transaction direction, not a separate model), so
/// budget math and search work identically for both. <see cref="Description"/> is PII — spending
/// patterns reveal sensitive facts (GLBA's "nonpublic personal information" reaches this far).
/// A transaction that exists has already passed its gate: the tools that create them are either
/// approval-gated (imports, admin entries, AI categorization) or the one deliberate
/// quick-capture exception (ADR-0005, a member logging their own spending).
/// </summary>
public sealed class Transaction : TenantEntityBase
{
    public Guid AccountId { get; set; }

    public DateOnly OccurredOn { get; set; }

    /// <summary>Always positive; <see cref="Direction"/> decides the sign applied to balances.</summary>
    public decimal Amount { get; set; }

    /// <summary>ISO 4217 — matches the account's currency (enforced at the tool layer).</summary>
    public required string CurrencyCode { get; set; }

    /// <summary>Merchant / narrative line (PII).</summary>
    public required string Description { get; set; }

    /// <summary>Null until categorized (AI-suggested, human-approved).</summary>
    public Guid? CategoryId { get; set; }

    /// <summary>income | expense.</summary>
    public required string Direction { get; set; }

    /// <summary>
    /// manual | assistant | upload | plaid — where this transaction came from. "manual" is a
    /// human typing into a form; "assistant" is any chat-tool write (the model chose the
    /// arguments, so the AI origin must stay visible — issue #49); "upload" is a line born
    /// from a human-approved statement batch; "plaid" is bank sync (its dedup filters on the
    /// value). Rows written before the "assistant" value existed keep "manual".
    /// </summary>
    public required string Source { get; set; }

    public Guid? CreatedByUserId { get; set; }

    /// <summary>Normalizes free-text directions to the two the module speaks.</summary>
    public static string? NormalizeDirection(string? direction) => direction?.Trim().ToLowerInvariant() switch
    {
        "expense" or "spend" or "spending" or "debit" or "out" => "expense",
        "income" or "deposit" or "credit" or "in" or "earning" => "income",
        _ => null,
    };

    /// <summary>The delta this transaction applies to its account's cached balance.</summary>
    public decimal BalanceDelta => Direction == "income" ? Amount : -Amount;
}

using Cortex.Core.Entities;

namespace Networthy.Finance.Persistence;

/// <summary>
/// A savings goal — the forward-looking half of budgets (SPEC differentiator). Two honest
/// progress models: a goal LINKED to an account reads that account's balance (the money is
/// where the money is); an unlinked goal tracks <see cref="SavedAmount"/> moved by explicit,
/// approval-gated contributions. Contributions are bookkeeping markers, never transactions —
/// they don't touch balances, so nothing double-counts.
/// </summary>
public sealed class Goal : TenantEntityBase
{
    public required string Name { get; set; }

    public decimal TargetAmount { get; set; }

    /// <summary>ISO 4217, e.g. "USD".</summary>
    public required string CurrencyCode { get; set; }

    /// <summary>Optional deadline — enables "on pace / behind" verdicts.</summary>
    public DateOnly? TargetDate { get; set; }

    /// <summary>Linked account whose balance IS the progress. Null = manual tracking.</summary>
    public Guid? AccountId { get; set; }

    /// <summary>Progress for unlinked goals; moved only by contribute_to_goal.</summary>
    public decimal SavedAmount { get; set; }

    public Guid? CreatedByUserId { get; set; }
}

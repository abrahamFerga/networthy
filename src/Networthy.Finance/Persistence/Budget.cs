using Plenipo.Core.Entities;

namespace Networthy.Finance.Persistence;

/// <summary>
/// A monthly spending target for one category. One row per category per month per currency —
/// history is kept, so "how did we do in May" always has an answer. Spent-vs-target is computed
/// from approved transactions at read time, never cached (a corrected transaction corrects the
/// budget view automatically).
/// </summary>
public sealed class Budget : TenantEntityBase
{
    public Guid CategoryId { get; set; }

    /// <summary>The first day of the month this target applies to.</summary>
    public DateOnly PeriodMonth { get; set; }

    public decimal TargetAmount { get; set; }

    /// <summary>ISO 4217 — targets are per currency, like everything else.</summary>
    public required string CurrencyCode { get; set; }
}

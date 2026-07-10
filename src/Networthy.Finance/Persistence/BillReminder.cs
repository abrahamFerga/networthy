using Cortex.Core.Entities;

namespace Networthy.Finance.Persistence;

/// <summary>
/// The bill-reminder ledger: one row per (merchant, expected date) that has already been
/// notified, so the sweep is idempotent across ticks and restarts. Append-only bookkeeping,
/// never user-facing.
/// </summary>
public sealed class BillReminder : TenantEntityBase
{
    public required string MerchantKey { get; set; }

    public DateOnly ExpectedOn { get; set; }
}

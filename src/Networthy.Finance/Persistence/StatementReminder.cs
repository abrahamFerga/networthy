using Cortex.Core.Entities;

namespace Networthy.Finance.Persistence;

/// <summary>
/// The statement-reminder ledger: one row per (tenant, period) that has already been nudged, so
/// the recurring job is idempotent across a same-period catch-up run (issue #71). The scheduler
/// ticks daily, but the reminder fires once per household-configured period — a marker here is
/// what makes the second run of the same period a no-op instead of a duplicate nudge. Append-only
/// bookkeeping, never user-facing (the mirror image of <see cref="BillReminder"/>).
/// </summary>
public sealed class StatementReminder : TenantEntityBase
{
    /// <summary>The first day of the reminded period (month start, or week's Monday for weekly).</summary>
    public DateOnly PeriodStart { get; set; }
}

using Cortex.Core.Entities;

namespace Networthy.Finance.Persistence;

/// <summary>
/// The household's preferences — one row per tenant, created on first write. Two of these are
/// load-bearing everywhere: <see cref="DefaultCurrencyCode"/> is what every tool means when no
/// currency is spoken, and <see cref="TimeZoneId"/> defines the household's "today" (transaction
/// dates, budget month boundaries, snapshot days, reminder timing) — without it, an evening
/// purchase in Mexico City lands on tomorrow's UTC date.
/// </summary>
public sealed class HouseholdSettings : TenantEntityBase
{
    /// <summary>ISO 4217 — the currency the household thinks in (default USD).</summary>
    public string DefaultCurrencyCode { get; set; } = "USD";

    /// <summary>IANA time zone id (e.g. "America/Mexico_City"). Null = UTC.</summary>
    public string? TimeZoneId { get; set; }

    /// <summary>How many days before an expected recurring charge the reminder fires.</summary>
    public int BillReminderLeadDays { get; set; } = 3;

    /// <summary>
    /// Whether the household gets the recurring statement-upload reminder (issue #71). On by
    /// default — a manual-upload household that never gets nudged simply lets its data go stale;
    /// individual members can still mute the <c>finance.statements</c> category for themselves.
    /// </summary>
    public bool StatementRemindersEnabled { get; set; } = true;

    /// <summary>
    /// How often the statement reminder fires: <c>monthly</c> (the default — statements arrive
    /// monthly) or <c>weekly</c>. The recurring job ticks daily but emits once per period, so this
    /// is the period the per-tenant marker is keyed on, not the scheduler's cadence.
    /// </summary>
    public string StatementReminderCadence { get; set; } = "monthly";

    /// <summary>Emergency-fund guideline floor, in months of average expenses (health assessment).</summary>
    public decimal EmergencyFundFloorMonths { get; set; } = 3m;

    /// <summary>APR at/above this counts as "high-interest" debt (health assessment / avalanche).</summary>
    public decimal HighAprThresholdPercent { get; set; } = 8m;

    /// <summary>The household's current date in its own time zone (UTC when unset/invalid).</summary>
    public DateOnly Today() => TodayIn(TimeZoneId);

    /// <summary>Normalizes free-text statement-reminder cadences to the two the module speaks.</summary>
    public static string? NormalizeStatementCadence(string? cadence) => cadence?.Trim().ToLowerInvariant() switch
    {
        "monthly" or "every month" => "monthly",
        "weekly" or "every week" => "weekly",
        _ => null,
    };

    /// <summary>Pure helper shared with the tenant-blind sweeps.</summary>
    public static DateOnly TodayIn(string? timeZoneId)
    {
        if (!string.IsNullOrWhiteSpace(timeZoneId) &&
            TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var zone))
        {
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone));
        }

        return DateOnly.FromDateTime(DateTime.UtcNow);
    }
}

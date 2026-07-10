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

    /// <summary>The household's current date in its own time zone (UTC when unset/invalid).</summary>
    public DateOnly Today() => TodayIn(TimeZoneId);

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

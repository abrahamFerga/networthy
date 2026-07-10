using System.ComponentModel;
using Cortex.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Networthy.Finance.Persistence;

namespace Networthy.Finance;

/// <summary>
/// Read and change the household's preferences. Updates are record changes and approval-gated
/// like every other write — the default currency and the household's clock shape every number
/// this product reports.
/// </summary>
public sealed class HouseholdSettingsTools(
    FinanceDbContext db,
    ITenantContext tenant)
{
    [Description("The household's preferences: default currency, time zone, and bill-reminder lead time.")]
    public async Task<string> GetHouseholdSettings(CancellationToken cancellationToken = default)
    {
        var settings = await db.HouseholdSettings.FirstOrDefaultAsync(cancellationToken);
        return settings is null
            ? "No preferences saved yet — defaults apply: currency USD, time zone UTC, reminders 3 days ahead. " +
              "Change them with update_household_settings or the Settings tab."
            : $"Default currency: {settings.DefaultCurrencyCode}. " +
              $"Time zone: {settings.TimeZoneId ?? "UTC"} (today there: {settings.Today():yyyy-MM-dd}). " +
              $"Bill reminders: {settings.BillReminderLeadDays} day(s) ahead.";
    }

    [Description("Change the household's preferences: default currency (ISO), time zone (IANA id like 'America/Mexico_City'), and/or bill-reminder lead days. Side-effecting and requires approval.")]
    public async Task<string> UpdateHouseholdSettings(
        [Description("ISO currency the household thinks in, e.g. MXN. Omit to leave unchanged.")] string? defaultCurrency = null,
        [Description("IANA time zone id, e.g. 'America/Mexico_City'. Omit to leave unchanged; 'UTC' resets.")] string? timeZone = null,
        [Description("Days before an expected recurring charge to remind (0–14). Omit to leave unchanged.")] int? billReminderLeadDays = null,
        CancellationToken cancellationToken = default)
    {
        if (defaultCurrency is null && timeZone is null && billReminderLeadDays is null)
        {
            return "Nothing to change — pass defaultCurrency, timeZone, and/or billReminderLeadDays.";
        }

        if (defaultCurrency is not null && defaultCurrency.Trim().Length != 3)
        {
            return $"'{defaultCurrency}' is not an ISO currency code (e.g. USD, MXN, EUR).";
        }

        string? normalizedZone = null;
        if (!string.IsNullOrWhiteSpace(timeZone) && !timeZone.Trim().Equals("UTC", StringComparison.OrdinalIgnoreCase))
        {
            if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZone.Trim(), out var zone))
            {
                return $"'{timeZone}' is not a time zone I know — use an IANA id like 'America/Mexico_City'.";
            }

            normalizedZone = zone.Id;
        }

        if (billReminderLeadDays is < 0 or > 14)
        {
            return "billReminderLeadDays must be between 0 and 14.";
        }

        var settings = await db.HouseholdSettings.FirstOrDefaultAsync(cancellationToken);
        if (settings is null)
        {
            settings = new HouseholdSettings { TenantId = tenant.RequireTenantId() };
            db.HouseholdSettings.Add(settings);
        }

        if (defaultCurrency is not null)
        {
            settings.DefaultCurrencyCode = defaultCurrency.Trim().ToUpperInvariant();
        }

        if (timeZone is not null)
        {
            settings.TimeZoneId = normalizedZone; // null when the caller said UTC
        }

        if (billReminderLeadDays is { } lead)
        {
            settings.BillReminderLeadDays = lead;
        }

        await db.SaveChangesAsync(cancellationToken);
        return $"Preferences saved: currency {settings.DefaultCurrencyCode}, " +
               $"time zone {settings.TimeZoneId ?? "UTC"} (today there: {settings.Today():yyyy-MM-dd}), " +
               $"reminders {settings.BillReminderLeadDays} day(s) ahead.";
    }
}

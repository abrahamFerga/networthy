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
        var rates = await db.ExchangeRates.OrderBy(r => r.CurrencyCode).ToListAsync(cancellationToken);
        var ratesLine = rates.Count == 0
            ? " No exchange rates saved (set_exchange_rate to combine multi-currency totals)."
            : " Exchange rates: " + string.Join(", ",
                rates.Select(r => $"1 {r.CurrencyCode} = {r.RateToDefault:0.####} {settings?.DefaultCurrencyCode ?? "USD"}")) + ".";
        return settings is null
            ? "No preferences saved yet — defaults apply: currency USD, time zone UTC, reminders 3 days ahead, " +
              "monthly statement reminders on, emergency-fund floor 3 months, high-APR threshold 8%. " +
              "Change them with update_household_settings or the Settings tab." + ratesLine
            : $"Default currency: {settings.DefaultCurrencyCode}. " +
              $"Time zone: {settings.TimeZoneId ?? "UTC"} (today there: {settings.Today():yyyy-MM-dd}). " +
              $"Bill reminders: {settings.BillReminderLeadDays} day(s) ahead. " +
              $"Statement reminders: {(settings.StatementRemindersEnabled ? $"{settings.StatementReminderCadence}" : "off")}. " +
              $"Emergency-fund floor: {settings.EmergencyFundFloorMonths:0.#} months. " +
              $"High-APR threshold: {settings.HighAprThresholdPercent:0.##}%." + ratesLine;
    }

    [Description("Change the household's preferences: default currency (ISO), time zone (IANA id like 'America/Mexico_City'), bill-reminder lead days, and/or the statement-reminder schedule. Side-effecting and requires approval.")]
    public async Task<string> UpdateHouseholdSettings(
        [Description("ISO currency the household thinks in, e.g. MXN. Omit to leave unchanged.")] string? defaultCurrency = null,
        [Description("IANA time zone id, e.g. 'America/Mexico_City'. Omit to leave unchanged; 'UTC' resets.")] string? timeZone = null,
        [Description("Days before an expected recurring charge to remind (0–14). Omit to leave unchanged.")] int? billReminderLeadDays = null,
        [Description("Emergency-fund guideline floor in months of expenses (0–24). Omit to leave unchanged.")] decimal? emergencyFundFloorMonths = null,
        [Description("APR percent at/above which debt counts as high-interest (0–100). Omit to leave unchanged.")] decimal? highAprThresholdPercent = null,
        [Description("Turn the recurring statement-upload reminder on or off. Omit to leave unchanged.")] bool? statementReminders = null,
        [Description("How often the statement reminder fires: 'monthly' or 'weekly'. Omit to leave unchanged.")] string? statementReminderCadence = null,
        CancellationToken cancellationToken = default)
    {
        if (defaultCurrency is null && timeZone is null && billReminderLeadDays is null &&
            emergencyFundFloorMonths is null && highAprThresholdPercent is null &&
            statementReminders is null && statementReminderCadence is null)
        {
            return "Nothing to change — pass defaultCurrency, timeZone, billReminderLeadDays, " +
                   "emergencyFundFloorMonths, highAprThresholdPercent, statementReminders, " +
                   "and/or statementReminderCadence.";
        }

        string? normalizedStatementCadence = null;
        if (statementReminderCadence is not null)
        {
            normalizedStatementCadence = HouseholdSettings.NormalizeStatementCadence(statementReminderCadence);
            if (normalizedStatementCadence is null)
            {
                return $"'{statementReminderCadence}' is not a statement-reminder cadence — use 'monthly' or 'weekly'.";
            }
        }

        if (emergencyFundFloorMonths is < 0 or > 24)
        {
            return "emergencyFundFloorMonths must be between 0 and 24.";
        }

        if (highAprThresholdPercent is < 0 or > 100)
        {
            return "highAprThresholdPercent must be between 0 and 100.";
        }

        // Membership in the real ISO set, not just "3 letters" — a typo'd code stored here would
        // silently exclude every account from the household-currency-scoped reads (net worth,
        // safe-to-spend, charts). Same list the Settings/onboarding pickers offer.
        if (defaultCurrency is not null &&
            !FinanceModule.CurrencyCodes.Contains(defaultCurrency.Trim().ToUpperInvariant()))
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

        if (emergencyFundFloorMonths is { } floor)
        {
            settings.EmergencyFundFloorMonths = floor;
        }

        if (highAprThresholdPercent is { } aprThreshold)
        {
            settings.HighAprThresholdPercent = aprThreshold;
        }

        if (statementReminders is { } remindersOn)
        {
            settings.StatementRemindersEnabled = remindersOn;
        }

        if (normalizedStatementCadence is not null)
        {
            settings.StatementReminderCadence = normalizedStatementCadence;
        }

        await db.SaveChangesAsync(cancellationToken);
        return $"Preferences saved: currency {settings.DefaultCurrencyCode}, " +
               $"time zone {settings.TimeZoneId ?? "UTC"} (today there: {settings.Today():yyyy-MM-dd}), " +
               $"reminders {settings.BillReminderLeadDays} day(s) ahead, " +
               $"statement reminders {(settings.StatementRemindersEnabled ? settings.StatementReminderCadence : "off")}, " +
               $"emergency floor {settings.EmergencyFundFloorMonths:0.#} months, " +
               $"high-APR at or above {settings.HighAprThresholdPercent:0.##}%.";
    }

    [Description("Save the household's own conversion rate for a foreign currency (units of the default currency per 1 unit of it), so multi-currency net worth can be combined. Rates are user-chosen — never fetched behind your back. Side-effecting and requires approval.")]
    public async Task<string> SetExchangeRate(
        [Description("The foreign ISO currency, e.g. USD.")] string currency,
        [Description("Units of the household's DEFAULT currency per 1 unit of it, e.g. 17.05.")] decimal rateToDefault,
        CancellationToken cancellationToken = default)
    {
        var code = currency?.Trim().ToUpperInvariant();
        if (code is not { Length: 3 })
        {
            return $"'{currency}' is not an ISO currency code (e.g. USD, EUR).";
        }

        if (rateToDefault <= 0)
        {
            return "rateToDefault must be positive.";
        }

        var settings = await db.HouseholdSettings.FirstOrDefaultAsync(cancellationToken);
        var defaultCurrency = settings?.DefaultCurrencyCode ?? "USD";
        if (string.Equals(code, defaultCurrency, StringComparison.Ordinal))
        {
            return $"{code} IS the household's default currency — its rate is 1 by definition.";
        }

        var rate = await db.ExchangeRates.FirstOrDefaultAsync(r => r.CurrencyCode == code, cancellationToken);
        if (rate is null)
        {
            rate = new ExchangeRate { TenantId = tenant.RequireTenantId(), CurrencyCode = code };
            db.ExchangeRates.Add(rate);
        }

        rate.RateToDefault = rateToDefault;
        await db.SaveChangesAsync(cancellationToken);
        return $"Saved: 1 {code} = {rateToDefault:0.####} {defaultCurrency}. " +
               "Multi-currency totals (get_net_worth) now combine using this rate.";
    }
}

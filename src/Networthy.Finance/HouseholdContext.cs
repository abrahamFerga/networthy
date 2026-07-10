using Microsoft.EntityFrameworkCore;
using Networthy.Finance.Persistence;

namespace Networthy.Finance;

/// <summary>
/// The scoped answer to "what currency does this household mean?" and "what day is it for
/// them?" — resolved once per request from <see cref="HouseholdSettings"/> (defaults when the
/// household never saved any). Tools and endpoints take this instead of hardcoding "USD" and
/// the UTC day.
/// </summary>
public sealed class HouseholdContext(FinanceDbContext db)
{
    private HouseholdSettings? _cached;

    public async Task<HouseholdSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        _cached ??= await db.HouseholdSettings.FirstOrDefaultAsync(cancellationToken)
            ?? new HouseholdSettings { TenantId = Guid.Empty }; // detached defaults; never saved

    /// <summary>The effective currency: the caller's explicit choice, else the household default.</summary>
    public async Task<string> ResolveCurrencyAsync(string? currency, CancellationToken cancellationToken = default) =>
        string.IsNullOrWhiteSpace(currency)
            ? (await GetSettingsAsync(cancellationToken)).DefaultCurrencyCode
            : currency.Trim().ToUpperInvariant();

    /// <summary>The household's current date in its own time zone.</summary>
    public async Task<DateOnly> TodayAsync(CancellationToken cancellationToken = default) =>
        (await GetSettingsAsync(cancellationToken)).Today();
}

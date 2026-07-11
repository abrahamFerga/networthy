using Cortex.Core.Entities;

namespace Networthy.Finance.Persistence;

/// <summary>
/// A household-saved conversion rate: how many units of the household's DEFAULT currency one
/// unit of <see cref="CurrencyCode"/> is worth (e.g. default MXN, USD -> 17.0). User-supplied
/// on purpose — the product never fetches market rates behind the household's back, so every
/// combined number is traceable to a rate someone chose.
/// </summary>
public sealed class ExchangeRate : TenantEntityBase
{
    /// <summary>The foreign ISO currency, uppercase (never the default currency itself).</summary>
    public required string CurrencyCode { get; set; }

    /// <summary>Units of the household default currency per 1 unit of <see cref="CurrencyCode"/>.</summary>
    public decimal RateToDefault { get; set; }
}

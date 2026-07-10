using Cortex.Core.Entities;

namespace Networthy.Finance.Persistence;

/// <summary>
/// A declared, recurring income — "2,500 every two weeks from ACME". The schedule the household
/// EXPECTS, distinct from the income transactions that actually arrive: cash-flow capacity and
/// goal planning ("how much per paycheck?") need the cadence, not just history.
/// </summary>
public sealed class IncomeSource : TenantEntityBase
{
    public required string Name { get; set; }

    /// <summary>The amount received each time (per paycheck, not per month).</summary>
    public decimal Amount { get; set; }

    /// <summary>ISO 4217, e.g. "USD".</summary>
    public required string CurrencyCode { get; set; }

    /// <summary>weekly | biweekly (every two weeks) | semimonthly (twice a month) | monthly.</summary>
    public required string Cadence { get; set; }

    /// <summary>Optional account the income lands in.</summary>
    public Guid? AccountId { get; set; }

    public Guid? CreatedByUserId { get; set; }

    /// <summary>Normalizes free-text cadences to the four the module speaks.</summary>
    public static string? NormalizeCadence(string? cadence) => cadence?.Trim().ToLowerInvariant() switch
    {
        "weekly" or "every week" => "weekly",
        "biweekly" or "bi-weekly" or "every two weeks" or "fortnightly" => "biweekly",
        "semimonthly" or "semi-monthly" or "twice a month" or "1st and 15th" => "semimonthly",
        "monthly" or "every month" => "monthly",
        _ => null,
    };

    /// <summary>Paychecks per year for a cadence.</summary>
    public static decimal PaychecksPerYear(string cadence) => cadence switch
    {
        "weekly" => 52m,
        "biweekly" => 26m,
        "semimonthly" => 24m,
        _ => 12m,
    };

    /// <summary>The monthly-equivalent amount (biweekly ≠ twice a month: 26 pays, not 24).</summary>
    public decimal MonthlyEquivalent => Amount * PaychecksPerYear(Cadence) / 12m;
}

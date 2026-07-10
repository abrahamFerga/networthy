using System.Text;

namespace Networthy.Finance;

/// <summary>A detected recurring charge — computed from transaction history, never stored.</summary>
public sealed record RecurringCharge(
    string MerchantKey,
    string DisplayName,
    string Cadence,
    decimal AverageAmount,
    decimal LastAmount,
    DateOnly LastSeen,
    DateOnly NextExpected,
    int Occurrences)
{
    /// <summary>True when the latest charge is meaningfully above the running average.</summary>
    public bool PriceRisen => LastAmount > AverageAmount * 1.08m;

    /// <summary>The charge restated per month (a weekly 10 costs ~43.33/month).</summary>
    public decimal MonthlyCost => Cadence switch
    {
        "weekly" => AverageAmount * 52m / 12m,
        "biweekly" => AverageAmount * 26m / 12m,
        "monthly" => AverageAmount,
        "quarterly" => AverageAmount / 3m,
        "yearly" => AverageAmount / 12m,
        _ => AverageAmount,
    };
}

/// <summary>
/// Pure recurring-charge detection over expense history — subscriptions, bills, memberships.
/// Deliberately conservative: at least three occurrences, near-regular intervals, similar
/// amounts. A miss is a charge the user finds manually; a false positive is a tool that cries
/// wolf — the thresholds prefer the miss.
/// </summary>
public static class RecurringDetection
{
    /// <summary>One expense observation.</summary>
    public sealed record Observation(DateOnly Date, decimal Amount, string Description);

    /// <summary>
    /// Merchant identity: uppercase, digits and reference noise stripped, whitespace collapsed.
    /// "NETFLIX.COM 880-1234" and "Netflix.com 880-9999" are the same subscription.
    /// </summary>
    public static string NormalizeMerchant(string description)
    {
        var sb = new StringBuilder(description.Length);
        foreach (var c in description.ToUpperInvariant())
        {
            if (char.IsDigit(c) || c is '#' or '*' or '-' or '_')
            {
                continue;
            }

            sb.Append(c);
        }

        var collapsed = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length > 40 ? collapsed[..40].TrimEnd() : collapsed;
    }

    /// <summary>Median day-gap → a cadence name, or null when the rhythm matches nothing we trust.</summary>
    internal static string? ClassifyGap(double medianDays) => medianDays switch
    {
        >= 5 and <= 9 => "weekly",
        >= 12 and <= 16 => "biweekly",
        >= 26 and <= 35 => "monthly",
        >= 80 and <= 100 => "quarterly",
        >= 330 and <= 400 => "yearly",
        _ => null,
    };

    public static IReadOnlyList<RecurringCharge> Detect(IEnumerable<Observation> expenses)
    {
        var results = new List<RecurringCharge>();
        foreach (var group in expenses
                     .GroupBy(e => NormalizeMerchant(e.Description))
                     .Where(g => g.Key.Length >= 3))
        {
            var ordered = group.OrderBy(e => e.Date).ToList();
            if (ordered.Count < 3)
            {
                continue;
            }

            var gaps = new List<int>();
            for (var i = 1; i < ordered.Count; i++)
            {
                gaps.Add(ordered[i].Date.DayNumber - ordered[i - 1].Date.DayNumber);
            }

            var sortedGaps = gaps.OrderBy(g => g).ToList();
            var median = sortedGaps.Count % 2 == 1
                ? (double)sortedGaps[sortedGaps.Count / 2]
                : (sortedGaps[sortedGaps.Count / 2 - 1] + sortedGaps[sortedGaps.Count / 2]) / 2.0;
            var cadence = ClassifyGap(median);
            if (cadence is null)
            {
                continue;
            }

            // Every gap must stay near the rhythm — one wild gap means "same store, not a bill".
            if (gaps.Any(g => g < median * 0.7 || g > median * 1.3))
            {
                continue;
            }

            // Amounts must be in the same family (price rises allowed, wild swings are shopping).
            var amounts = ordered.Select(e => e.Amount).ToList();
            if (amounts.Min() <= 0 || amounts.Max() / amounts.Min() > 1.35m)
            {
                continue;
            }

            var last = ordered[^1];
            results.Add(new RecurringCharge(
                group.Key,
                last.Description.Trim(),
                cadence,
                Math.Round(amounts.Average(), 2),
                last.Amount,
                last.Date,
                last.Date.AddDays((int)Math.Round(median)),
                ordered.Count));
        }

        return [.. results.OrderByDescending(r => r.MonthlyCost)];
    }
}

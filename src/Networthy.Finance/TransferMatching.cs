using System.Globalization;
using Networthy.Finance.Persistence;

namespace Networthy.Finance;

/// <summary>
/// One suggested pairing: money that left <see cref="Outgoing"/>'s account appearing in
/// <see cref="Incoming"/>'s. <see cref="HighConfidence"/> is the "link these without further
/// questions once the human approves" tier; everything else is shown but never bulk-linked.
/// </summary>
public sealed record TransferCandidate(
    Transaction Outgoing,
    Transaction Incoming,
    bool HighConfidence,
    string Reason);

/// <summary>
/// Pure inter-account transfer detection, unit-tested without a database. The shape it hunts:
/// an expense in one account and an income in another, days apart, that are really ONE movement
/// (SPEC v2.1 — the owner's Payoneer USD → MXN tax account → bills account chain). Deliberately
/// conservative, same philosophy as <see cref="RecurringDetection"/>: a missed pair is one the
/// human links by hand; a wrong auto-suggestion erodes exactly the trust the approval gate
/// exists to protect. Cross-currency plausibility comes ONLY from the household's own saved
/// rates (never a fetched market rate); with no saved rate, both legs must say "transfer" in
/// their own words. The matcher only ever SUGGESTS — a human confirms every link.
/// </summary>
public static class TransferMatching
{
    /// <summary>How many days after the money left it may arrive (cross-border runs slow).</summary>
    public const int DateWindowDays = 5;

    /// <summary>Posting-date skew tolerance: the receiving bank may stamp a day EARLIER.</summary>
    public const int DateSkewDays = 1;

    /// <summary>Saved rates are a lens, not a market feed — allow this much drift + FX spread.</summary>
    public const decimal CrossCurrencyTolerance = 0.05m;

    /// <summary>Same-currency legs whose amounts differ by up to this (a wire/withdrawal fee)
    /// still pair when a description says "transfer" — never on amounts alone.</summary>
    public const decimal FeeTolerance = 0.02m;

    /// <summary>
    /// Description tokens (uppercased, EN + es-MX) that mean "this line is a transfer":
    /// TRANSFER covers transferencia/transferwise; WITHDRAW covers withdrawal; SPEI is Mexico's
    /// interbank rail; RETIRO/TRASPASO/ENVIO the Spanish forms; PAYONEER the owner's inbound rail.
    /// Deliberately short — "DEPOSIT" would tag every payroll line.
    /// </summary>
    private static readonly string[] Signals =
        ["TRANSFER", "TRASPASO", "SPEI", "WIRE", "WITHDRAW", "RETIRO", "ENVIO", "ENVÍO", "PAYONEER"];

    /// <summary>True when the description carries a transfer-shaped token.</summary>
    public static bool LooksLikeTransfer(string description)
    {
        var upper = description.ToUpperInvariant();
        return Signals.Any(upper.Contains);
    }

    /// <summary>
    /// Suggested one-to-one pairings over <paramref name="unlinked"/> (rows already carrying a
    /// transfer link must not be offered again — pass them filtered out). Greedy assignment,
    /// best evidence first, each transaction used at most once; deterministic order.
    /// <paramref name="ratesToDefault"/> is the household's saved lens: units of
    /// <paramref name="defaultCurrency"/> per 1 unit of the keyed currency.
    /// </summary>
    public static IReadOnlyList<TransferCandidate> FindCandidates(
        IReadOnlyList<Transaction> unlinked,
        IReadOnlyDictionary<Guid, Account> accountsById,
        IReadOnlyDictionary<string, decimal> ratesToDefault,
        string defaultCurrency)
    {
        var outgoing = unlinked
            .Where(t => t.Direction == "expense" && t.Amount > 0 && accountsById.ContainsKey(t.AccountId))
            .ToList();
        var incoming = unlinked
            .Where(t => t.Direction == "income" && t.Amount > 0 && accountsById.ContainsKey(t.AccountId))
            .ToList();
        if (outgoing.Count == 0 || incoming.Count == 0)
        {
            return [];
        }

        // Same-currency exactness is only "high" when the pairing is unambiguous — two 500.00
        // candidates in the window mean the matcher cannot know which is which, so it says so.
        var scored = new List<(TransferCandidate Candidate, decimal AmountDrift, int DayGap)>();
        foreach (var outLeg in outgoing)
        {
            foreach (var inLeg in incoming)
            {
                if (inLeg.AccountId == outLeg.AccountId)
                {
                    continue; // a transfer needs two pockets
                }

                var gap = inLeg.OccurredOn.DayNumber - outLeg.OccurredOn.DayNumber;
                if (gap < -DateSkewDays || gap > DateWindowDays)
                {
                    continue;
                }

                var evidence = Evaluate(outLeg, inLeg, ratesToDefault, defaultCurrency);
                if (evidence is { } e)
                {
                    scored.Add((new TransferCandidate(outLeg, inLeg, e.High, e.Reason), e.Drift, Math.Abs(gap)));
                }
            }
        }

        // Ambiguity demotion: an exact-amount leg with several possible counterparts is honest
        // "likely", never bulk-linked high confidence.
        var byOutgoing = scored.GroupBy(s => s.Candidate.Outgoing.Id).ToDictionary(g => g.Key, g => g.Count());
        var byIncoming = scored.GroupBy(s => s.Candidate.Incoming.Id).ToDictionary(g => g.Key, g => g.Count());
        var demoted = scored
            .Select(s => byOutgoing[s.Candidate.Outgoing.Id] > 1 || byIncoming[s.Candidate.Incoming.Id] > 1
                ? s with
                {
                    Candidate = s.Candidate with
                    {
                        HighConfidence = false,
                        Reason = s.Candidate.Reason + "; several possible counterparts — confirm the right one by description",
                    },
                }
                : s)
            .ToList();

        // Greedy one-to-one: strongest evidence first, then the tightest amounts and dates;
        // OccurredOn/Id last so the order (and tests) are deterministic.
        var used = new HashSet<Guid>();
        var result = new List<TransferCandidate>();
        foreach (var (candidate, _, _) in demoted
                     .OrderByDescending(s => s.Candidate.HighConfidence)
                     .ThenBy(s => s.AmountDrift)
                     .ThenBy(s => s.DayGap)
                     .ThenBy(s => s.Candidate.Outgoing.OccurredOn)
                     .ThenBy(s => s.Candidate.Outgoing.Id))
        {
            if (used.Contains(candidate.Outgoing.Id) || used.Contains(candidate.Incoming.Id))
            {
                continue;
            }

            used.Add(candidate.Outgoing.Id);
            used.Add(candidate.Incoming.Id);
            result.Add(candidate);
        }

        return result;
    }

    /// <summary>The evidence one (outgoing, incoming) pair rests on, or null when there is none.</summary>
    private static (bool High, string Reason, decimal Drift)? Evaluate(
        Transaction outLeg,
        Transaction inLeg,
        IReadOnlyDictionary<string, decimal> ratesToDefault,
        string defaultCurrency)
    {
        var signaled = LooksLikeTransfer(outLeg.Description) || LooksLikeTransfer(inLeg.Description);
        var days = Math.Abs(inLeg.OccurredOn.DayNumber - outLeg.OccurredOn.DayNumber);
        var apart = days == 0 ? "same day" : days == 1 ? "1 day apart" : $"{days} days apart";

        if (outLeg.CurrencyCode.Equals(inLeg.CurrencyCode, StringComparison.OrdinalIgnoreCase))
        {
            if (outLeg.Amount == inLeg.Amount)
            {
                return (true, $"same amount, {apart}", 0m);
            }

            var drift = Math.Abs(outLeg.Amount - inLeg.Amount) / outLeg.Amount;
            if (drift <= FeeTolerance && signaled)
            {
                return (false,
                    $"amounts differ by a fee-sized {Math.Abs(outLeg.Amount - inLeg.Amount).ToString("N2", CultureInfo.InvariantCulture)} " +
                    $"{outLeg.CurrencyCode.ToUpperInvariant()}, {apart}, transfer-shaped description", drift);
            }

            return null;
        }

        // Cross-currency: only the household's own saved rates may vouch for the amounts.
        if (TryConvertToDefault(outLeg, ratesToDefault, defaultCurrency, out var outInDefault) &&
            TryConvertToDefault(inLeg, ratesToDefault, defaultCurrency, out var inInDefault) &&
            outInDefault > 0)
        {
            var drift = Math.Abs(outInDefault - inInDefault) / outInDefault;
            if (drift <= CrossCurrencyTolerance)
            {
                var reason = $"amounts agree with your saved exchange rate within {drift:P1}, {apart}";
                return signaled ? (true, reason + ", transfer-shaped description", drift) : (false, reason, drift);
            }

            return null;
        }

        // No usable rate: never invent one. Both legs must say "transfer" in words.
        if (LooksLikeTransfer(outLeg.Description) && LooksLikeTransfer(inLeg.Description))
        {
            return (false,
                $"no saved {outLeg.CurrencyCode.ToUpperInvariant()}→{inLeg.CurrencyCode.ToUpperInvariant()} rate to check " +
                $"the amounts — matched on transfer-shaped descriptions {apart} (set_exchange_rate tightens this)", 1m);
        }

        return null;
    }

    /// <summary>The leg's amount in the household default currency, through saved rates only.</summary>
    private static bool TryConvertToDefault(
        Transaction leg, IReadOnlyDictionary<string, decimal> ratesToDefault, string defaultCurrency, out decimal converted)
    {
        var currency = leg.CurrencyCode.ToUpperInvariant();
        if (currency.Equals(defaultCurrency, StringComparison.OrdinalIgnoreCase))
        {
            converted = leg.Amount;
            return true;
        }

        if (ratesToDefault.TryGetValue(currency, out var rate) && rate > 0)
        {
            converted = leg.Amount * rate;
            return true;
        }

        converted = 0;
        return false;
    }
}

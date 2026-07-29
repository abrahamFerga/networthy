using Networthy.Finance.Persistence;

namespace Networthy.Finance;

/// <summary>
/// Same money, imported twice. A household that uploads the bank's PDF and then its CSV export
/// for the same period is describing ONE set of transactions in two files; approving both must
/// not double the ledger. This is the content identity that decides it, kept deterministic and
/// pure so the rule is testable and explainable to the person approving.
/// <para>
/// The comparison is deliberately conservative: date, signed direction, amount to the cent, and
/// the description after case/whitespace normalization must all agree. Two files that print the
/// same purchase with genuinely different narratives will NOT be recognized here — the
/// duplicate-period warning still fires for that, and a human decides. That asymmetry is on
/// purpose: silently dropping a real transaction is a worse failure than posting a duplicate the
/// reviewer can see.
/// </para>
/// </summary>
internal static class StatementDeduplication
{
    /// <summary>
    /// The identity two rows must share to be the same movement of money. Whitespace runs collapse
    /// and case folds because the same statement rendered as PDF text and exported as CSV differs
    /// in spacing far more often than in substance; nothing else about the description is thrown
    /// away, so distinct merchants stay distinct.
    /// </summary>
    internal static string ContentKey(DateOnly date, decimal amount, string direction, string description) =>
        $"{date:yyyyMMdd}|{direction.Trim().ToLowerInvariant()}|{amount:0.00}|{NormalizeDescription(description)}";

    internal static string ContentKey(ExtractedLine line) =>
        ContentKey(line.Date, line.Amount, line.Direction, line.Description);

    internal static string ContentKey(Transaction transaction) =>
        ContentKey(transaction.OccurredOn, transaction.Amount, transaction.Direction, transaction.Description);

    /// <summary>
    /// Splits a batch's lines into the ones that still need posting and the ones an existing
    /// transaction already covers, preserving order.
    /// <para>
    /// Occurrences are COUNTED, not merely seen: if the account already holds one 4.50 coffee on
    /// the 3rd and this statement lists two, the second still posts. A household really can buy
    /// the same thing twice in a day, and a dedup that collapses that is quietly wrong in the
    /// direction that loses money.
    /// </para>
    /// </summary>
    internal static (IReadOnlyList<ExtractedLine> ToPost, IReadOnlyList<ExtractedLine> AlreadyPosted) Partition(
        IReadOnlyList<ExtractedLine> lines, IEnumerable<string> existingKeys)
    {
        var remaining = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var key in existingKeys)
        {
            remaining[key] = remaining.GetValueOrDefault(key) + 1;
        }

        var toPost = new List<ExtractedLine>();
        var alreadyPosted = new List<ExtractedLine>();
        foreach (var line in lines)
        {
            var key = ContentKey(line);
            if (remaining.TryGetValue(key, out var count) && count > 0)
            {
                remaining[key] = count - 1;
                alreadyPosted.Add(line);
            }
            else
            {
                toPost.Add(line);
            }
        }

        return (toPost, alreadyPosted);
    }

    private static string NormalizeDescription(string description)
    {
        var normalized = new System.Text.StringBuilder(description.Length);
        var pendingSpace = false;
        foreach (var ch in description)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = normalized.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                normalized.Append(' ');
                pendingSpace = false;
            }
            normalized.Append(char.ToUpperInvariant(ch));
        }

        return normalized.ToString();
    }
}

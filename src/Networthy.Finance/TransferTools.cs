using System.ComponentModel;
using System.Text;
using Plenipo.Core.Identity;
using Microsoft.EntityFrameworkCore;
using Networthy.Finance.Persistence;

namespace Networthy.Finance;

/// <summary>
/// Inter-account transfer linking (SPEC v2.1): the owner's money hops Payoneer USD → MXN tax
/// account → bills account, and each hop's two statement lines must become ONE transfer, not
/// income plus expense. <see cref="TransferMatching"/> finds the candidates; nothing links until
/// a human approves — and a linked pair keeps both balances exactly as posted, it only stops
/// counting as income/spending. Unlink restores the ordinary reading.
/// </summary>
public sealed class TransferTools(
    FinanceDbContext db,
    ICurrentUser currentUser,
    HouseholdContext household)
{
    private const int DefaultLookbackDays = 90;

    [Description("Scan recent transactions for pairs that look like money moved between the household's OWN accounts (an expense in one, the matching income in another, days apart — same currency or via the household's saved exchange rates). Read-only; nothing links without link_transfers.")]
    public async Task<string> SuggestTransferLinks(
        [Description("How many days back to scan (default 90, max 365).")] int lookbackDays = DefaultLookbackDays,
        CancellationToken cancellationToken = default)
    {
        var (candidates, accounts) = await FindCandidatesAsync(lookbackDays, cancellationToken);
        if (accounts.Count < 2)
        {
            return "Transfer matching needs at least two visible accounts — there is nowhere for money to move between.";
        }

        if (candidates.Count == 0)
        {
            return $"No unlinked transaction pairs in the last {Clamp(lookbackDays)} days look like inter-account " +
                   "transfers. Matching wants an expense in one account and a matching income in another within " +
                   $"{TransferMatching.DateWindowDays} days — equal amounts in the same currency, amounts that agree " +
                   "with a saved exchange rate across currencies (set_exchange_rate), or transfer-shaped " +
                   "descriptions (SPEI, transferencia, withdrawal…).";
        }

        var sb = new StringBuilder($"{candidates.Count} candidate transfer pair(s), strongest first:\n");
        foreach (var candidate in candidates)
        {
            sb.AppendLine(FormatCandidate(candidate, accounts));
        }

        var high = candidates.Count(c => c.HighConfidence);
        sb.Append(high > 0
            ? $"link_transfers links the {high} HIGH pair(s) once you approve; "
            : "No pair is high-confidence, so nothing bulk-links; ");
        sb.Append("link_transfers with an outgoing and an incoming description links one specific pair; " +
                  "unlink_transfer undoes a link.");
        return sb.ToString();
    }

    [Description("Link transfer pair(s) so they stop counting as income/spending (balances stay exactly as posted). With NO arguments: links every high-confidence candidate at once. With BOTH descriptions: links that one pair. Side-effecting and requires approval; reversible with unlink_transfer.")]
    public async Task<string> LinkTransfers(
        [Description("Optional: the OUTGOING (expense) leg's description, or a distinctive part of it.")] string? outgoingDescription = null,
        [Description("Optional: the INCOMING (income) leg's description, or a distinctive part of it.")] string? incomingDescription = null,
        [Description("How many days back to scan in bulk mode (default 90, max 365).")] int lookbackDays = DefaultLookbackDays,
        CancellationToken cancellationToken = default)
    {
        var hasOut = !string.IsNullOrWhiteSpace(outgoingDescription);
        var hasIn = !string.IsNullOrWhiteSpace(incomingDescription);
        if (hasOut != hasIn)
        {
            throw new ToolRefusalException(
                "Name BOTH legs (outgoingDescription and incomingDescription) to link one specific pair, " +
                "or neither to link every high-confidence candidate.");
        }

        return hasOut
            ? await LinkOnePairAsync(outgoingDescription!, incomingDescription!, cancellationToken)
            : await LinkHighConfidenceAsync(lookbackDays, cancellationToken);
    }

    [Description("Undo a transfer link: both legs return to counting as ordinary income/expense. Side-effecting and requires approval.")]
    public async Task<string> UnlinkTransfer(
        [Description("The description (or a distinctive part) of EITHER leg of the linked transfer.")] string descriptionMatch,
        CancellationToken cancellationToken = default)
    {
        var visible = await VisibleAccountsAsync(cancellationToken);
        var pattern = $"%{descriptionMatch.Trim()}%";
        var matches = (await db.Transactions
                .Where(t => t.TransferGroupId != null && EF.Functions.ILike(t.Description, pattern))
                .OrderByDescending(t => t.OccurredOn)
                .Take(10)
                .ToListAsync(cancellationToken))
            .Where(t => visible.ContainsKey(t.AccountId))
            .ToList();
        if (matches.Count == 0)
        {
            throw new ToolRefusalException(
                $"No LINKED transfer leg matches '{descriptionMatch}'. search_transactions shows which rows " +
                "are marked as transfers.");
        }

        // Two hits that are each other's counterpart ARE one unambiguous transfer.
        var groups = matches.Select(t => t.TransferGroupId).Distinct().ToList();
        if (groups.Count > 1)
        {
            var sb = new StringBuilder($"{groups.Count} different linked transfers match '{descriptionMatch}' — be more specific:\n");
            foreach (var m in matches.Take(5))
            {
                sb.AppendLine($"- {m.OccurredOn:yyyy-MM-dd} · {Signed(m)} · {m.Description} — {visible[m.AccountId].Name}");
            }

            throw new ToolRefusalException(sb.ToString());
        }

        var legs = await db.Transactions
            .Where(t => t.TransferGroupId == groups[0])
            .ToListAsync(cancellationToken);
        foreach (var leg in legs)
        {
            leg.TransferGroupId = null;
        }

        await db.SaveChangesAsync(cancellationToken);
        var described = string.Join(" ⇄ ", legs
            .OrderByDescending(l => l.Direction) // expense (outgoing) first, income second
            .Select(l => $"{Signed(l)} '{l.Description}'"));
        return $"Unlinked: {described}. Both legs count as ordinary income/expense again " +
               "(they had no category since linking — categorize_transaction if they need one).";
    }

    // ── The shared candidate scan (chat tools, the import-approval hook, and the tab action) ──

    internal async Task<(IReadOnlyList<TransferCandidate> Candidates, IReadOnlyDictionary<Guid, Account> Accounts)>
        FindCandidatesAsync(int lookbackDays, CancellationToken cancellationToken)
    {
        var accounts = await VisibleAccountsAsync(cancellationToken);
        if (accounts.Count < 2)
        {
            return ([], accounts);
        }

        var today = await household.TodayAsync(cancellationToken);
        var since = today.AddDays(-Clamp(lookbackDays));
        var unlinked = (await db.Transactions
                .Where(t => t.TransferGroupId == null && t.OccurredOn >= since)
                .ToListAsync(cancellationToken))
            .Where(t => accounts.ContainsKey(t.AccountId))
            .ToList();

        var defaultCurrency = await household.ResolveCurrencyAsync(null, cancellationToken);
        var rates = (await db.ExchangeRates.ToListAsync(cancellationToken))
            .ToDictionary(r => r.CurrencyCode.ToUpperInvariant(), r => r.RateToDefault);

        return (TransferMatching.FindCandidates(unlinked, accounts, rates, defaultCurrency), accounts);
    }

    /// <summary>
    /// The statement-approval follow-up: candidates that touch the batch's account, phrased for
    /// the end of approve_import_batch's message. Empty string when there is nothing to say —
    /// the approval message must not grow noise.
    /// </summary>
    internal async Task<string> DescribePostApprovalCandidatesAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var (candidates, accounts) = await FindCandidatesAsync(DefaultLookbackDays, cancellationToken);
        var touching = candidates
            .Where(c => c.Outgoing.AccountId == accountId || c.Incoming.AccountId == accountId)
            .Take(5)
            .ToList();
        if (touching.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder($"\n{touching.Count} posted line(s) look like transfers between your accounts:\n");
        foreach (var candidate in touching)
        {
            sb.AppendLine(FormatCandidate(candidate, accounts));
        }

        sb.Append("Nothing is linked yet — link_transfers links the high-confidence pair(s) after your approval, " +
                  "so they stop counting as income/spending.");
        return sb.ToString();
    }

    /// <summary>Bulk mode: link every high-confidence pair. Shared by the chat tool and the tab action.</summary>
    internal async Task<string> LinkHighConfidenceAsync(int lookbackDays, CancellationToken cancellationToken)
    {
        var (candidates, accounts) = await FindCandidatesAsync(lookbackDays, cancellationToken);
        var high = candidates.Where(c => c.HighConfidence).ToList();
        if (high.Count == 0)
        {
            // Deliberately NOT a ToolRefusalException (issue #182): a bulk sweep that scanned the
            // window and found nothing to link is an empty-but-valid outcome, like a search with no
            // hits — not a failed precondition. Reporting it as a failure would relabel working
            // behaviour as broken, which is the mirror image of the defect #182 fixed. The same
            // reasoning keeps POST /transfers/link-detected a 200 for this case.
            return "No high-confidence transfer pairs to link right now. suggest_transfer_links shows the " +
                   "near-misses and what evidence each is missing.";
        }

        var cleared = 0;
        var sb = new StringBuilder($"Linked {high.Count} transfer(s):\n");
        foreach (var candidate in high)
        {
            cleared += Link(candidate.Outgoing, candidate.Incoming);
            sb.AppendLine(FormatCandidate(candidate, accounts, withReason: false));
        }

        await db.SaveChangesAsync(cancellationToken);
        sb.Append("These no longer count as income or spending; balances are unchanged.");
        if (cleared > 0)
        {
            sb.Append(" Their categories were cleared — a transfer is neither income nor spending.");
        }

        sb.Append(" unlink_transfer undoes any of them.");
        return sb.ToString();
    }

    private async Task<string> LinkOnePairAsync(
        string outgoingDescription, string incomingDescription, CancellationToken cancellationToken)
    {
        var visible = await VisibleAccountsAsync(cancellationToken);
        var outgoing = await ResolveLegAsync(outgoingDescription, "expense", visible, cancellationToken);
        if (outgoing.Error is { } outError)
        {
            throw new ToolRefusalException(outError);
        }

        var incoming = await ResolveLegAsync(incomingDescription, "income", visible, cancellationToken);
        if (incoming.Error is { } inError)
        {
            throw new ToolRefusalException(inError);
        }

        var outLeg = outgoing.Leg!;
        var inLeg = incoming.Leg!;
        if (outLeg.AccountId == inLeg.AccountId)
        {
            throw new ToolRefusalException(
                "Both legs sit on the SAME account — a transfer needs money leaving one account and " +
                "arriving in another. Pick legs from two different accounts.");
        }

        var cleared = Link(outLeg, inLeg);
        await db.SaveChangesAsync(cancellationToken);

        // The human said these two are one movement; oddities are worth naming, not blocking.
        var caveat = "";
        if (outLeg.CurrencyCode.Equals(inLeg.CurrencyCode, StringComparison.OrdinalIgnoreCase)
            && outLeg.Amount != inLeg.Amount)
        {
            caveat = $" Note: the amounts differ ({outLeg.Amount:N2} vs {inLeg.Amount:N2} {outLeg.CurrencyCode}) — " +
                     "fine if a fee was taken, worth a second look if not.";
        }

        return $"Linked as one transfer: {Signed(outLeg)} '{outLeg.Description}' ({visible[outLeg.AccountId].Name}) ⇄ " +
               $"{Signed(inLeg)} '{inLeg.Description}' ({visible[inLeg.AccountId].Name}). " +
               "Neither counts as income/spending anymore; balances are unchanged." +
               (cleared > 0 ? " Their categories were cleared." : "") + caveat +
               " unlink_transfer undoes this.";
    }

    /// <summary>Stamps one fresh pair id on both legs; returns how many category tags were cleared.</summary>
    private static int Link(Transaction outLeg, Transaction inLeg)
    {
        var group = Guid.NewGuid();
        var cleared = (outLeg.CategoryId is null ? 0 : 1) + (inLeg.CategoryId is null ? 0 : 1);
        outLeg.TransferGroupId = group;
        inLeg.TransferGroupId = group;
        outLeg.CategoryId = null; // a transfer is neither income nor spending — a category would
        inLeg.CategoryId = null;  // silently drag it back into budget math
        return cleared;
    }

    private async Task<(Transaction? Leg, string? Error)> ResolveLegAsync(
        string descriptionMatch, string direction, IReadOnlyDictionary<Guid, Account> visible,
        CancellationToken cancellationToken)
    {
        var label = direction == "expense" ? "outgoing (expense)" : "incoming (income)";
        var pattern = $"%{descriptionMatch.Trim()}%";
        var matches = (await db.Transactions
                .Where(t => t.TransferGroupId == null && t.Direction == direction
                            && EF.Functions.ILike(t.Description, pattern))
                .OrderByDescending(t => t.OccurredOn)
                .Take(10)
                .ToListAsync(cancellationToken))
            .Where(t => visible.ContainsKey(t.AccountId))
            .Take(5)
            .ToList();

        if (matches.Count == 1)
        {
            return (matches[0], null);
        }

        if (matches.Count == 0)
        {
            return (null, $"No unlinked {label} transaction matches '{descriptionMatch}'. " +
                          "search_transactions finds the exact wording; already-linked legs need unlink_transfer first.");
        }

        var sb = new StringBuilder($"{matches.Count} unlinked {label} transactions match '{descriptionMatch}' — be more specific:\n");
        foreach (var m in matches)
        {
            sb.AppendLine($"- {m.OccurredOn:yyyy-MM-dd} · {Signed(m)} · {m.Description} — {visible[m.AccountId].Name}");
        }

        return (null, sb.ToString());
    }

    private async Task<Dictionary<Guid, Account>> VisibleAccountsAsync(CancellationToken cancellationToken) =>
        (await db.Accounts.ToListAsync(cancellationToken))
        .Where(a => a.IsVisibleTo(currentUser.UserId))
        .ToDictionary(a => a.Id);

    private static string FormatCandidate(
        TransferCandidate candidate, IReadOnlyDictionary<Guid, Account> accounts, bool withReason = true)
    {
        var outLeg = candidate.Outgoing;
        var inLeg = candidate.Incoming;
        var line = $"- {Signed(outLeg)} '{outLeg.Description}' ({accounts[outLeg.AccountId].Name}, {outLeg.OccurredOn:yyyy-MM-dd}) → " +
                   $"{Signed(inLeg)} '{inLeg.Description}' ({accounts[inLeg.AccountId].Name}, {inLeg.OccurredOn:yyyy-MM-dd})";
        return withReason
            ? $"{line} — {(candidate.HighConfidence ? "HIGH" : "possible")}: {candidate.Reason}"
            : line;
    }

    private static string Signed(Transaction t) =>
        $"{(t.Direction == "income" ? "+" : "-")}{t.Amount:N2} {t.CurrencyCode.ToUpperInvariant()}";

    private static int Clamp(int lookbackDays) => Math.Clamp(lookbackDays, 7, 365);
}

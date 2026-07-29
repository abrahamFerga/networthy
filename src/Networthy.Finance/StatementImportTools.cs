using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Plenipo.Application.Files;
using Plenipo.Application.Jobs;
using Plenipo.Core.Identity;
using Plenipo.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Networthy.Finance.Persistence;

namespace Networthy.Finance;

/// <summary>
/// The statement-import pipeline (SPEC must-have #1): upload → extraction job → human review →
/// approved lines become Transactions. ONE approval gate, placed where it matters: importing
/// runs ungated (it only queues extraction into the review pipeline, and discard undoes it),
/// and NOTHING posts until the batch is approved — reviewing the extracted lines is the
/// product's core "AI drafts, human decides" moment. Two gates was approval ceremony squared.
/// </summary>
public sealed class StatementImportTools(
    FinanceDbContext db,
    IFileStore files,
    IJobQueue jobs,
    ITenantContext tenant,
    ICurrentUser currentUser,
    TransferTools transfers)
{
    [Description("Import an uploaded bank statement (CSV/OFX/QFX/PDF; the file id comes from the message's attachment block). The account name is OPTIONAL: leave it out and Networthy detects which account the statement belongs to (asking before creating anything). Extraction runs in the background; review with review_import_batch before anything posts. Runs without an approval prompt: it only queues extraction into the review pipeline — approving the reviewed batch is the gate, and discard_import_batch undoes an unwanted import.")]
    public async Task<string> ImportStatement(
        [Description("The stored file id (a GUID) of the uploaded statement.")] string fileId,
        [Description("Optional: the account name this statement belongs to. Omit to auto-detect from the statement itself.")] string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(fileId, out var id))
        {
            return $"'{fileId}' is not a file id. Attach the statement, or use list_documents to find it.";
        }

        var stored = await files.FindAsync(id, cancellationToken);
        if (stored is null)
        {
            return $"No stored file with id {id} exists. Attach the statement first.";
        }

        Account? account = null;
        if (!string.IsNullOrWhiteSpace(accountName))
        {
            account = await db.Accounts.FirstOrDefaultAsync(
                a => EF.Functions.ILike(a.Name, accountName.Trim()), cancellationToken);
            if (account is null || !account.IsVisibleTo(currentUser.UserId))
            {
                // An explicit name that misses is far more often a typo than an intent to create:
                // suggest instead of importing into limbo, and offer the detect path.
                var visible = await VisibleAccountsAsync(cancellationToken);
                var closest = SuggestAccountNames(accountName, visible);
                return $"No account named '{accountName}' exists (or it is private to another member)."
                       + (closest.Count > 0 ? $" Did you mean {string.Join(" or ", closest.Select(n => $"'{n}'"))}?" : "")
                       + " Re-run with an existing name, or import WITHOUT an account and Networthy will "
                       + "detect it from the statement (and ask before creating anything).";
            }
        }

        var batch = new StatementImportBatch
        {
            TenantId = tenant.RequireTenantId(),
            AccountId = account?.Id,
            SourceFileId = stored.Id,
            FileName = stored.FileName,
            Status = "queued",
            CreatedByUserId = currentUser.UserId,
        };
        db.ImportBatches.Add(batch);
        await db.SaveChangesAsync(cancellationToken);

        await jobs.EnqueueAsync(FinanceModule.Id, StatementParseJobHandler.JobKind,
            new StatementParseArgs(batch.Id), cancellationToken);

        return account is null
            ? $"Import of '{stored.FileName}' is queued for extraction. Networthy will detect which account " +
              "it belongs to — an unambiguous match is used directly; otherwise the batch waits and " +
              "assign_import_account picks or creates the account. Nothing posts until you approve."
            : $"Import of '{stored.FileName}' into '{account.Name}' is queued for extraction. " +
              "Once parsed, review the lines with review_import_batch — nothing posts until you approve.";
    }

    [Description("Attach an account to an import batch that is waiting for one (status needs-account): name an existing account, or set createIfMissing to create it — the new account inherits the statement's detected institution and masked number. Side-effecting and requires approval.")]
    public async Task<string> AssignImportAccount(
        [Description("The account to import into (existing, or the name for a new one).")] string accountName,
        [Description("Create the account when no existing one matches the name. Default false.")] bool createIfMissing = false,
        [Description("Type for a NEWLY created account: checking, savings, credit, cash, or loan. Default checking.")] string? accountType = null,
        [Description("Optional file name (or part of it) to pick a specific waiting batch; defaults to the most recent.")] string? fileName = null,
        CancellationToken cancellationToken = default)
    {
        var trimmed = accountName.Trim();
        if (trimmed.Length == 0)
        {
            return "Give the account a name — an existing account's, or the name the new one should have.";
        }

        var batch = await FindBatchAsync(fileName, cancellationToken, status: "needs-account");
        if (batch is null)
        {
            return "No import batch is waiting for an account. list_import_batches shows what's pending.";
        }

        return (await AssignAccountAsync(batch, trimmed, createIfMissing, accountType, cancellationToken)).Message;
    }

    /// <summary>
    /// The assignment core, shared by the chat tool (which resolves the batch by file name and
    /// may create the account) and the review-page endpoint (which resolves by id and only picks
    /// existing accounts — its picker lists them, so a miss is a race, not a typo).
    /// </summary>
    internal async Task<(bool Ok, string Message)> AssignAccountAsync(
        StatementImportBatch batch, string accountName, bool createIfMissing, string? accountType,
        CancellationToken cancellationToken)
    {
        var account = await db.Accounts.FirstOrDefaultAsync(
            a => EF.Functions.ILike(a.Name, accountName), cancellationToken);
        if (account is not null && !account.IsVisibleTo(currentUser.UserId))
        {
            return (false, $"'{account.Name}' is private to another member — pick a different account.");
        }

        if (account is null)
        {
            if (!createIfMissing)
            {
                var visible = await VisibleAccountsAsync(cancellationToken);
                var closest = SuggestAccountNames(accountName, visible);
                return (false, $"No account named '{accountName}' exists."
                       + (closest.Count > 0 ? $" Did you mean {string.Join(" or ", closest.Select(n => $"'{n}'"))}?" : "")
                       + $" To create it for this statement ({DetectedLabel(batch)}), use assign_import_account with createIfMissing.");
            }

            account = await CreateAccountFromDetectionAsync(batch, accountName, accountType, cancellationToken);
        }

        batch.AccountId = account.Id;
        batch.Status = "parsed";
        await db.SaveChangesAsync(cancellationToken);

        return (true, $"'{batch.FileName}' will import into '{account.Name}'. " +
               "Review the lines — nothing posts until you approve.");
    }

    [Description("Show an import batch's extracted lines (dates, amounts, suggested categories) for review before approval. Defaults to the most recent batch.")]
    public async Task<string> ReviewImportBatch(
        [Description("Optional file name (or part of it) to pick a specific batch.")] string? fileName = null,
        CancellationToken cancellationToken = default)
    {
        var batch = await FindBatchAsync(fileName, cancellationToken);
        if (batch is null)
        {
            return "No import batches yet. Attach a statement and run import_statement.";
        }

        switch (batch.Status)
        {
            case "queued":
                return $"'{batch.FileName}' is still being extracted — ask again in a moment.";
            case "failed":
                return $"'{batch.FileName}' failed extraction: {batch.FailureReason}";
            case "approved":
                return $"'{batch.FileName}' was already approved and posted.";
        }

        var lines = Deserialize(batch.ExtractedLinesJson);
        if (lines.Count == 0)
        {
            return $"'{batch.FileName}' parsed but produced no lines — the file may be empty or a summary-only export.";
        }

        var sb = new StringBuilder();
        if (batch.Status == "needs-account")
        {
            // The "ask before creating" moment: say what the statement looks like and lay out
            // both resolutions — an existing account, or creating the detected one.
            sb.AppendLine($"'{batch.FileName}' looks like a statement from {DetectedLabel(batch)}, and no " +
                          "existing account matches. Before these lines can be reviewed for posting, pick where " +
                          "they belong: assign_import_account with an existing account's name, or with " +
                          "createIfMissing to create it (the new account inherits the detected institution " +
                          "and masked number).");
        }

        sb.AppendLine($"Extracted from '{batch.FileName}' ({lines.Count} line(s))" +
                      (batch.Status == "parsed" ? " — review, then approve_import_batch:" : ":"));
        foreach (var line in lines.Take(40))
        {
            sb.AppendLine($"- {line.Date:yyyy-MM-dd} · {(line.Direction == "income" ? "+" : "-")}{line.Amount:N2} · {line.Description}" +
                          $"{(line.SuggestedCategory is null ? "" : $" [suggest: {line.SuggestedCategory}]")}");
        }

        if (lines.Count > 40)
        {
            sb.AppendLine($"… and {lines.Count - 40} more.");
        }

        var expense = lines.Where(l => l.Direction == "expense").Sum(l => l.Amount);
        var income = lines.Where(l => l.Direction == "income").Sum(l => l.Amount);
        sb.Append($"Totals: -{expense:N2} expense, +{income:N2} income.");

        var overlap = await FindOverlappingApprovedBatchAsync(batch, cancellationToken);
        if (overlap is not null)
        {
            sb.Append($"\n⚠ Heads-up: '{overlap.FileName}' was already approved for this account covering " +
                      $"{overlap.LinesFrom:yyyy-MM-dd} → {overlap.LinesTo:yyyy-MM-dd}, which overlaps this " +
                      "statement's period — approving both may post the same transactions twice.");
        }

        return sb.ToString();
    }

    [Description("List every import batch that has not been approved yet (file name, when it was imported, status, line count). Read-only — use this to enumerate what's pending when several statements are in flight, then pick one by file name for review_import_batch or approve_import_batch.")]
    public async Task<string> ListImportBatches(CancellationToken cancellationToken = default)
    {
        var visibleAccountIds = db.Accounts
            .Where(a => a.RestrictedToUserId == null || a.RestrictedToUserId == currentUser.UserId)
            .Select(a => a.Id);
        var batches = await db.ImportBatches
            // Account-less batches are pre-assignment by definition — nothing account-private to hide.
            .Where(b => b.Status != "approved"
                        && (b.AccountId == null || visibleAccountIds.Contains(b.AccountId.Value)))
            .OrderByDescending(b => b.CreatedAt)
            .Take(50)
            .ToListAsync(cancellationToken);
        if (batches.Count == 0)
        {
            return "No import batches are pending. Attach a statement and run import_statement.";
        }

        var sb = new StringBuilder($"{batches.Count} pending import batch(es), newest first:\n");
        foreach (var batch in batches)
        {
            var detail = batch.Status switch
            {
                "parsed" => $"{Deserialize(batch.ExtractedLinesJson).Count} line(s) awaiting review",
                "queued" => "still being extracted",
                "needs-account" => $"needs an account — looks like {DetectedLabel(batch)}; resolve with assign_import_account",
                "failed" => $"failed: {batch.FailureReason}",
                _ => batch.Status,
            };
            sb.AppendLine($"- '{batch.FileName}' · imported {batch.CreatedAt:yyyy-MM-dd HH:mm} UTC · {detail}");
        }

        sb.Append("Pick one by file name with review_import_batch; nothing posts until approve_import_batch.");
        return sb.ToString();
    }

    [Description("Discard an UNPOSTED import batch (queued, parsed, needs-account, or failed) — a duplicate upload, or a statement the household decided not to post. The uploaded file stays in the file store and posted data is never touched; an approved batch cannot be discarded. Side-effecting and requires approval.")]
    public async Task<string> DiscardImportBatch(
        [Description("File name (or part of it) of the batch to discard; defaults to the most recent batch.")] string? fileName = null,
        CancellationToken cancellationToken = default)
    {
        var batch = await FindBatchAsync(fileName, cancellationToken);
        if (batch is null)
        {
            return "No import batches to discard. list_import_batches shows what exists.";
        }
        if (batch.Status == "approved")
        {
            // Approved batches are history: their period feeds the duplicate-period warning, and
            // their lines already posted. Removing the record would silently disarm both.
            return $"'{batch.FileName}' was approved and its lines are posted — an approved batch " +
                   "cannot be discarded. Correct individual transactions with edit_transaction instead.";
        }

        var status = batch.Status;
        db.ImportBatches.Remove(batch);
        await db.SaveChangesAsync(cancellationToken);
        return $"Discarded '{batch.FileName}' ({status}). Nothing had posted, so no account changed; " +
               "the uploaded file itself is still stored and can be re-imported.";
    }

    [Description("Approve a reviewed import batch: its lines post as transactions (with the suggested categories) and the account balance updates. Side-effecting and requires approval.")]
    public async Task<string> ApproveImportBatch(
        [Description("Optional file name (or part of it) to pick a specific batch; defaults to the most recent parsed one.")] string? fileName = null,
        CancellationToken cancellationToken = default)
    {
        // No name = "the one awaiting approval": resolve the newest PARSED batch, as documented.
        // Resolving the newest batch of ANY status made the default refuse on an already-approved
        // newer upload while a parsed one sat waiting.
        var batch = string.IsNullOrWhiteSpace(fileName)
            ? await FindBatchAsync(null, cancellationToken, status: "parsed")
            : await FindBatchAsync(fileName, cancellationToken);
        if (batch is null)
        {
            return string.IsNullOrWhiteSpace(fileName)
                ? "No parsed batch is awaiting approval. list_import_batches shows every batch and its status."
                : "No import batches yet. Attach a statement and run import_statement.";
        }

        return (await ApproveBatchAsync(batch, cancellationToken)).Message;
    }

    /// <summary>The approval core, shared by the chat tool (which resolves by file name) and the
    /// review endpoints (which resolve the latest parsed batch, or one by id). Ok=false is a
    /// refusal — the endpoints turn it into an error status so the UI can't mistake it for
    /// success.</summary>
    internal async Task<(bool Ok, string Message)> ApproveBatchAsync(StatementImportBatch batch, CancellationToken cancellationToken)
    {
        if (batch.Status == "needs-account" || batch.AccountId is null)
        {
            return (false, $"'{batch.FileName}' has no account yet (it looks like {DetectedLabel(batch)}). " +
                   "Assign one first — the Assign action on the review page, or assign_import_account in Chat " +
                   "— then review and approve.");
        }
        if (batch.Status != "parsed")
        {
            return (false, $"'{batch.FileName}' is {batch.Status} — only a parsed batch can be approved.");
        }

        var lines = Deserialize(batch.ExtractedLinesJson);
        if (lines.Count == 0)
        {
            return (false, $"'{batch.FileName}' has no lines to post.");
        }

        var account = await db.Accounts.FirstAsync(a => a.Id == batch.AccountId, cancellationToken);
        var categories = await db.Categories.ToListAsync(cancellationToken);
        var byName = categories.ToDictionary(c => c.Name, c => c.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            var transaction = new Transaction
            {
                TenantId = tenant.RequireTenantId(),
                AccountId = account.Id,
                OccurredOn = line.Date,
                Amount = line.Amount,
                CurrencyCode = account.CurrencyCode,
                Description = line.Description,
                CategoryId = line.SuggestedCategory is { } s && byName.TryGetValue(s, out var categoryId) ? categoryId : null,
                Direction = line.Direction,
                Source = "upload",
                CreatedByUserId = currentUser.UserId,
            };
            db.Transactions.Add(transaction);
            account.CachedBalance += transaction.BalanceDelta;
        }

        batch.Status = "approved";
        batch.ReviewedByUserId = currentUser.UserId;
        batch.ReviewedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        // The statement just landed in ONE account; if any of its lines mirror a line in another
        // account (the household's money changing pockets), say so now — the moment the user can
        // still see the statement in their head — rather than let income/spending quietly inflate.
        var transferFollowUp = await transfers.DescribePostApprovalCandidatesAsync(account.Id, cancellationToken);

        return (true, $"Posted {lines.Count} transaction(s) from '{batch.FileName}' to '{account.Name}'. " +
               $"New balance: {account.CachedBalance:N2} {account.CurrencyCode}." + transferFollowUp);
    }

    private async Task<StatementImportBatch?> FindBatchAsync(
        string? fileName, CancellationToken cancellationToken, string? status = null)
    {
        var query = db.ImportBatches.AsQueryable();
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var pattern = $"%{fileName.Trim()}%";
            query = query.Where(b => EF.Functions.ILike(b.FileName, pattern));
        }
        if (status is not null)
        {
            query = query.Where(b => b.Status == status);
        }

        return await query.OrderByDescending(b => b.CreatedAt).FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>"First Example Bank ••••1234" from what the parse job detected; honest when nothing was.</summary>
    internal static string DetectedLabel(StatementImportBatch batch) => ((batch.DetectedInstitution, batch.DetectedAccountMask) switch
    {
        ({ } bank, { } mask) => $"'{bank} {mask}'",
        ({ } bank, null) => $"'{bank}'",
        (null, { } mask) => $"account {mask}",
        _ => "an account the statement doesn't identify",
    }) + (batch.DetectedCurrency is null ? "" : $" ({batch.DetectedCurrency})");

    /// <summary>
    /// The auto-match the parse job trusts: the masked number's last four digits are decisive when
    /// exactly ONE visible account carries them; the institution name is accepted only when it
    /// singles out exactly one account. Anything ambiguous returns null — the flow asks instead.
    /// </summary>
    internal static Account? MatchAccount(IReadOnlyList<Account> candidates, DetectedAccountHint? hint)
    {
        if (hint is null)
        {
            return null;
        }

        if (hint.MaskLast4 is { } last4)
        {
            var byMask = candidates
                .Where(a => a.MaskedAccountNumber is { } mask
                            && new string(mask.Where(char.IsAsciiDigit).ToArray()).EndsWith(last4, StringComparison.Ordinal))
                .ToList();
            if (byMask.Count == 1)
            {
                return byMask[0];
            }
            if (byMask.Count > 1)
            {
                return null; // two accounts sharing a last-4 — never guess between them
            }
        }

        if (hint.Institution is { } institution)
        {
            var byInstitution = candidates
                .Where(a => (a.InstitutionName is { } held && Overlaps(held, institution))
                            || Overlaps(a.Name, institution))
                .ToList();
            if (byInstitution.Count == 1)
            {
                return byInstitution[0];
            }
        }

        return null;

        static bool Overlaps(string a, string b) =>
            a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Closest visible account names for "did you mean…" — containment first, small typos second.</summary>
    internal static IReadOnlyList<string> SuggestAccountNames(string requested, IReadOnlyList<Account> accounts)
    {
        var wanted = requested.Trim();
        return accounts
            .Select(a => (a.Name, Score: Score(wanted, a.Name)))
            .Where(x => x.Score < int.MaxValue)
            .OrderBy(x => x.Score)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .Select(x => x.Name)
            .ToList();

        static int Score(string wanted, string candidate)
        {
            if (candidate.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                || wanted.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }
            var distance = Levenshtein(wanted.ToLowerInvariant(), candidate.ToLowerInvariant());
            // Tolerate roughly one typo per five characters, capped — beyond that it isn't "close".
            return distance <= Math.Max(2, wanted.Length / 5) ? distance : int.MaxValue;
        }
    }

    private static int Levenshtein(string a, string b)
    {
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        var current = new int[b.Length + 1];
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(
                    Math.Min(previous[j] + 1, current[j - 1] + 1),
                    previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    /// <summary>
    /// Creates the account a needs-account batch detected, seeding institution and mask from the
    /// statement so the next import of the same statement auto-matches. Shared by the chat tool
    /// (human-chosen name) and the review page's row action (detected label as the name).
    /// </summary>
    internal async Task<Account> CreateAccountFromDetectionAsync(
        StatementImportBatch batch, string name, string? accountType, CancellationToken cancellationToken)
    {
        var settings = await db.HouseholdSettings.FirstOrDefaultAsync(cancellationToken);
        var account = new Account
        {
            TenantId = tenant.RequireTenantId(),
            Name = name,
            Type = Account.NormalizeType(accountType) ?? "checking",
            // The statement's own tagged currency beats the household default: a USD Payoneer
            // statement must not create an MXN account just because the household runs on MXN.
            CurrencyCode = batch.DetectedCurrency ?? settings?.DefaultCurrencyCode ?? "USD",
            InstitutionName = batch.DetectedInstitution,
            MaskedAccountNumber = batch.DetectedAccountMask,
            CachedBalance = 0,
            CreatedByUserId = currentUser.UserId,
        };
        db.Accounts.Add(account);
        return account;
    }

    /// <summary>An APPROVED batch for the same account whose line period overlaps this one's.</summary>
    private async Task<StatementImportBatch?> FindOverlappingApprovedBatchAsync(
        StatementImportBatch batch, CancellationToken cancellationToken)
    {
        if (batch.AccountId is null || batch.LinesFrom is null || batch.LinesTo is null)
        {
            return null;
        }

        return await db.ImportBatches
            .Where(b => b.Id != batch.Id
                        && b.AccountId == batch.AccountId
                        && b.Status == "approved"
                        && b.LinesFrom != null && b.LinesTo != null
                        && b.LinesFrom <= batch.LinesTo && batch.LinesFrom <= b.LinesTo)
            .OrderByDescending(b => b.ReviewedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private Task<List<Account>> VisibleAccountsAsync(CancellationToken cancellationToken) =>
        db.Accounts
            .Where(a => a.RestrictedToUserId == null || a.RestrictedToUserId == currentUser.UserId)
            .ToListAsync(cancellationToken);

    internal static IReadOnlyList<ExtractedLine> Deserialize(string? json) =>
        json is null
            ? []
            : JsonSerializer.Deserialize<List<ExtractedLine>>(json, JsonSerializerOptions.Web) ?? [];
}

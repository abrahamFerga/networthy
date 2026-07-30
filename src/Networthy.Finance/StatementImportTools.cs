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
    /// <summary>
    /// How long the chat tool waits for the parse job before honestly reporting "still running".
    /// The job queue polls at ~1s and a typical statement extracts in one to three seconds, so
    /// this budget turns almost every import into a same-turn outcome — the agent literally
    /// cannot "come back later" (nothing re-invokes it between user messages), so waiting here
    /// is the only place the outcome can reach the conversation.
    /// </summary>
    public static readonly TimeSpan ExtractionWaitBudget = TimeSpan.FromSeconds(8);

    [Description("Import an uploaded bank statement (CSV/OFX/QFX/PDF; the file id comes from the message's attachment block). The account name is OPTIONAL: leave it out and Networthy detects which account the statement belongs to (asking before creating anything). The tool waits briefly for extraction and usually returns the OUTCOME — parsed line count, a needs-account question, or a failure with its reason; a longer extraction keeps running in the background. Runs without an approval prompt: extraction only feeds the review pipeline — approving the reviewed batch is the gate, and discard_import_batch undoes an unwanted import.")]
    public async Task<string> ImportStatement(
        [Description("The stored file id (a GUID) of the uploaded statement.")] string fileId,
        [Description("Optional: the account name this statement belongs to. Omit to auto-detect from the statement itself.")] string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        var (batch, message) = await QueueImportAsync(fileId, accountName, cancellationToken);
        if (batch is null)
        {
            return message;
        }

        var finished = await WaitForExtractionAsync(batch.Id, cancellationToken);
        return finished is null
            ? message + " Extraction is STILL RUNNING (larger scans take a while) — the outcome lands on " +
              "the Statement review tab and in the notification bell; when the user next asks, " +
              "review_import_batch has the current state."
            : await DescribeExtractionOutcomeAsync(finished, cancellationToken);
    }

    /// <summary>
    /// Queue-only import for the upload endpoints (onboarding wizard, review page): returns the
    /// moment the batch is queued — a form upload must not hold its HTTP response hostage to an
    /// OCR run; the review tab and the notification bell carry the outcome there. The chat tool
    /// wraps this same core with a bounded wait, because chat's outcome channel IS the tool result.
    /// </summary>
    public async Task<string> QueueStatementImport(
        string fileId, string? accountName = null, CancellationToken cancellationToken = default)
    {
        var (_, message) = await QueueImportAsync(fileId, accountName, cancellationToken);
        return message;
    }

    private async Task<(StatementImportBatch? Batch, string Message)> QueueImportAsync(
        string fileId, string? accountName, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(fileId, out var id))
        {
            return (null, $"'{fileId}' is not a file id. Attach the statement, or use list_documents to find it.");
        }

        var stored = await files.FindAsync(id, cancellationToken);
        if (stored is null)
        {
            return (null, $"No stored file with id {id} exists. Attach the statement first.");
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
                return (null, $"No account named '{accountName}' exists (or it is private to another member)."
                       + (closest.Count > 0 ? $" Did you mean {string.Join(" or ", closest.Select(n => $"'{n}'"))}?" : "")
                       + " Re-run with an existing name, or import WITHOUT an account and Networthy will "
                       + "detect it from the statement (and ask before creating anything).");
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

        var message = account is null
            ? $"Import of '{stored.FileName}' is queued for extraction. Networthy will detect which account " +
              "it belongs to — an unambiguous match is used directly; otherwise the batch waits and " +
              "assign_import_account picks or creates the account. Nothing posts until you approve."
            : $"Import of '{stored.FileName}' into '{account.Name}' is queued for extraction. " +
              "Once parsed, review the lines with review_import_batch — nothing posts until you approve.";
        return (batch, message);
    }

    /// <summary>
    /// Polls (untracked — the job mutates the row in its OWN scope) until the batch leaves
    /// "queued" or the wait budget runs out. Null = still running when the budget expired.
    /// </summary>
    private async Task<StatementImportBatch?> WaitForExtractionAsync(Guid batchId, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + ExtractionWaitBudget;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            var current = await db.ImportBatches.AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == batchId, cancellationToken);
            if (current is not null && current.Status != "queued")
            {
                return current;
            }
        }

        return null;
    }

    /// <summary>The extraction outcome as the model should relay it — one message per terminal state.</summary>
    private async Task<string> DescribeExtractionOutcomeAsync(
        StatementImportBatch batch, CancellationToken cancellationToken)
    {
        if (batch.Status == "failed")
        {
            return $"Extraction of '{batch.FileName}' FAILED: {batch.FailureReason} " +
                   "Tell the user plainly. The batch sits on the Statement review tab; once the cause is " +
                   "fixed, import_statement with the same attachment retries it, and discard_import_batch " +
                   "drops it.";
        }

        var lines = Deserialize(batch.ExtractedLinesJson);
        var period = batch.LinesFrom is { } from && batch.LinesTo is { } to
            ? $" covering {from:yyyy-MM-dd} → {to:yyyy-MM-dd}"
            : "";
        var warning = batch.ReviewWarning is { Length: > 0 }
            ? $" Review warning: {batch.ReviewWarning}"
            : "";

        if (batch.Status == "needs-account")
        {
            return $"'{batch.FileName}' extracted {lines.Count} line(s){period} and looks like a statement " +
                   $"from {DetectedLabel(batch)}, but no existing account matches. Ask the user whether to " +
                   "use an existing account or create the detected one, then assign_import_account " +
                   $"(createIfMissing to create). Nothing posts until they approve.{warning}";
        }

        // "parsed" — extraction landed in an account (named up front, or auto-matched by detection).
        var accountName = batch.AccountId is { } accountId
            ? (await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken))?.Name
            : null;
        // The next step is to DISPLAY lines, which is read-only and needs nobody's permission — so
        // say that in the imperative and say it happens NOW. Phrasing it as "show them, and after
        // the user confirms, approve" made the model read one "confirms" as governing both verbs:
        // it announced the display as an offer, named the tool, and stopped, leaving the household
        // to type an internal API name to see their own statement.
        return $"'{batch.FileName}' extracted {lines.Count} line(s){period} into " +
               $"'{accountName ?? "(unknown account)"}'.{warning} " +
               "NEXT, IN THIS SAME TURN: fetch the extracted lines and show them to the user — that is " +
               "read-only, so do NOT ask permission and do NOT offer it as an option, just do it. " +
               "Only POSTING needs their explicit yes, and nothing posts until they give it.";
    }

    [Description("Attach an account to an import batch that is waiting for one (status needs-account): name an existing account, or set createIfMissing to create it — the new account inherits the statement's detected institution and masked number. An existing account with no number recorded learns the statement's, so its next statement matches automatically. Side-effecting and requires approval.")]
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

        // The human just answered which account this statement's number belongs to. Record that on
        // the account so the next statement matches itself and never asks again.
        var learned = AdoptDetectedIdentity(account, batch);

        batch.AccountId = account.Id;
        batch.Status = "parsed";
        await db.SaveChangesAsync(cancellationToken);

        return (true, $"'{batch.FileName}' will import into '{account.Name}'. " +
               (learned
                   ? $"Noted {DetectedLabel(batch)} on '{account.Name}', so the next statement for it matches automatically. "
                   : "") +
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
            return "No statements have been imported yet. Tell the user to attach a bank statement to " +
                   "a message, and import it for them when they do.";
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
                          "existing account matches. Ask the user where these lines belong — an account they " +
                          "already have, or a new one created from what the statement says (it would inherit " +
                          "the detected institution and masked number). Attach it to that account before " +
                          "anything can post; never create an account without the user saying so.");
        }

        sb.AppendLine($"Extracted from '{batch.FileName}' ({lines.Count} line(s)):");
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
                      "statement's period.");
        }

        // Say the exact count BEFORE approval, not just that periods overlap: the reviewer decides
        // knowing how much of this file is already in the ledger and that approving won't re-post it.
        if (batch.AccountId is { } accountId)
        {
            var (toPost, already) = await PartitionAgainstPostedAsync(accountId, lines, cancellationToken);
            if (already.Count > 0)
            {
                sb.Append($"\n{already.Count} of these {lines.Count} line(s) are already posted to this account — " +
                          $"approving will skip them and post the remaining {toPost.Count}, not double anything.");
            }
        }

        if (batch.Status == "parsed")
        {
            // Directed at the model, not the household: THIS is the point where a human yes is
            // required, and it is the only one in the import flow. Displaying lines needed no
            // permission and must not have been announced as if it did.
            sb.Append("\n(Show these lines to the user now and ask whether to post them. Posting is " +
                      "the gated step and needs their explicit yes.)");
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

    [Description("Approve a reviewed import batch: its lines post as transactions (with the suggested categories) and the account balance updates. Lines the account already holds (same date, amount and description) are skipped, so re-importing a period does not duplicate it. Side-effecting and requires approval.")]
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

        // The same month, imported twice — the bank's PDF and then its CSV export — describes ONE
        // set of transactions. Post only what this account does not already hold over the
        // statement's own span; the period warning at review time told the reviewer it might
        // happen, and this is what makes approving it safe rather than merely warned about.
        var (toPost, alreadyPosted) = await PartitionAgainstPostedAsync(account.Id, lines, cancellationToken);

        foreach (var line in toPost)
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

        var skipped = alreadyPosted.Count == 0
            ? ""
            : $" Skipped {alreadyPosted.Count} line(s) already posted to this account (same date, amount " +
              "and description), so importing this period twice didn't duplicate them.";

        if (toPost.Count == 0)
        {
            return (true, $"Nothing new to post from '{batch.FileName}' — all {lines.Count} line(s) were " +
                   $"already on '{account.Name}'. Balance unchanged at {account.CachedBalance:N2} {account.CurrencyCode}.");
        }

        // The statement just landed in ONE account; if any of its lines mirror a line in another
        // account (the household's money changing pockets), say so now — the moment the user can
        // still see the statement in their head — rather than let income/spending quietly inflate.
        var transferFollowUp = await transfers.DescribePostApprovalCandidatesAsync(account.Id, cancellationToken);

        return (true, $"Posted {toPost.Count} transaction(s) from '{batch.FileName}' to '{account.Name}'.{skipped} " +
               $"New balance: {account.CachedBalance:N2} {account.CurrencyCode}." + transferFollowUp);
    }

    /// <summary>
    /// Splits a batch's lines against what the account already holds over the lines' own date span.
    /// Shared by the review preview (which reports the count before anything posts) and the
    /// approval itself (which acts on it), so the two can never disagree.
    /// </summary>
    internal async Task<(IReadOnlyList<ExtractedLine> ToPost, IReadOnlyList<ExtractedLine> AlreadyPosted)>
        PartitionAgainstPostedAsync(Guid accountId, IReadOnlyList<ExtractedLine> lines, CancellationToken cancellationToken)
    {
        if (lines.Count == 0)
        {
            return ([], []);
        }

        var from = lines.Min(l => l.Date);
        var to = lines.Max(l => l.Date);
        var existing = await db.Transactions
            .Where(t => t.AccountId == accountId && t.OccurredOn >= from && t.OccurredOn <= to)
            .Select(t => new { t.OccurredOn, t.Amount, t.Direction, t.Description })
            .ToListAsync(cancellationToken);

        return StatementDeduplication.Partition(
            lines,
            existing.Select(t => StatementDeduplication.ContentKey(t.OccurredOn, t.Amount, t.Direction, t.Description)));
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

        var batch = await query.OrderByDescending(b => b.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (batch is not null)
        {
            // `db` is shared by every finance tool call within one chat turn (FinanceToolSource
            // resolves StatementImportTools once per turn), so a batch this SAME instance already
            // tracked earlier in the turn (e.g. import_statement's queue insert) wins EF's identity
            // resolution and would otherwise mask a later status the parse job committed through
            // its own, separate DbContext. Reload keeps the entity tracked (mutating callers below
            // still SaveChangesAsync normally) while guaranteeing its values are the database's own.
            await db.Entry(batch).ReloadAsync(cancellationToken);
        }

        return batch;
    }

    /// <summary>
    /// "Cuenta Priority ••••5597" / "First Example Bank ••••1234" from what the parse job detected;
    /// honest when nothing was. The statement's own name for the account leads when it printed one —
    /// it is what the household will recognise on the review tab.
    /// </summary>
    internal static string DetectedLabel(StatementImportBatch batch) => ((batch.DetectedAccountLabel ?? batch.DetectedInstitution, batch.DetectedAccountMask) switch
    {
        ({ } bank, { } mask) => $"'{bank} {mask}'",
        ({ } bank, null) => $"'{bank}'",
        (null, { } mask) => $"account {mask}",
        _ => "an account the statement doesn't identify",
    }) + (batch.DetectedCurrency is null ? "" : $" ({batch.DetectedCurrency})");

    /// <summary>
    /// The auto-match the parse job trusts: a last-four the statement printed is decisive when
    /// exactly ONE visible account carries it; the institution name is accepted only when it
    /// singles out exactly one account. Anything ambiguous returns null — the flow asks instead.
    /// <para>
    /// A statement usually prints more than one number for the same account (a card number and the
    /// account behind it), and the household recorded whichever one they recognise, so EVERY
    /// detected candidate is tried. Two candidates naming two different accounts is a contradiction,
    /// not a majority vote: it asks. A candidate no account carries costs nothing.
    /// </para>
    /// <para>
    /// When a rule leaves several accounts tied, the statement's own currency gets one chance to
    /// break the tie — the same four digits on an MXN account and a USD account identify different
    /// accounts, and the statement says which one it is. The tie-break only ever narrows a set that
    /// already returned "ask": every match the rules made before, they still make, unchanged.
    /// </para>
    /// </summary>
    internal static Account? MatchAccount(IReadOnlyList<Account> candidates, DetectedAccountHint? hint)
    {
        if (hint is null)
        {
            return null;
        }

        Account? resolved = null;
        var someNumberNamedAnAccount = false;
        foreach (var last4 in hint.AllLast4)
        {
            var byMask = candidates
                .Where(a => a.MaskedAccountNumber is { } mask
                            && new string(mask.Where(char.IsAsciiDigit).ToArray()).EndsWith(last4, StringComparison.Ordinal))
                .ToList();
            if (byMask.Count == 0)
            {
                continue;
            }

            someNumberNamedAnAccount = true;
            // Several accounts share this last-4. Only the statement's currency may separate them,
            // and only if it leaves exactly one — otherwise this candidate decides nothing.
            var pick = byMask.Count == 1 ? byMask[0] : NarrowByCurrency(byMask, hint.Currency);
            if (pick is null)
            {
                continue;
            }

            if (resolved is null)
            {
                resolved = pick;
            }
            else if (!ReferenceEquals(resolved, pick))
            {
                // The statement's own numbers disagree about where it belongs. Guessing here posts
                // a month of money to the wrong ledger.
                return null;
            }
        }

        if (resolved is not null)
        {
            return resolved;
        }

        // The numbers found accounts but none decisively. A weaker signal must not now decide what
        // a stronger, ambiguous one could not.
        if (someNumberNamedAnAccount)
        {
            return null;
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
            if (byInstitution.Count > 1)
            {
                // One bank, several accounts — the household's MXN and USD accounts at the same
                // institution are exactly this case, and the statement's currency names one.
                return NarrowByCurrency(byInstitution, hint.Currency);
            }
        }

        return null;

        static bool Overlaps(string a, string b) =>
            a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase);

        static Account? NarrowByCurrency(IReadOnlyList<Account> tied, string? currency)
        {
            if (currency is null)
            {
                return null;
            }

            var byCurrency = tied
                .Where(a => string.Equals(a.CurrencyCode, currency, StringComparison.OrdinalIgnoreCase))
                .ToList();
            return byCurrency.Count == 1 ? byCurrency[0] : null;
        }
    }

    /// <summary>
    /// Teaches an account the identity the statement carried, so the NEXT statement for it matches
    /// by number instead of asking again. Without this, a household that answers "it's my everyday
    /// checking" once is asked the very same question every month: the answer resolved the batch
    /// but the account itself never learned the masked number that would have matched it.
    /// <para>
    /// Only blanks are filled. A mask or institution the household typed is never overwritten —
    /// detection is best-effort and a human's answer outranks it. Currency is deliberately left
    /// alone: it is money math on an account that already holds transactions, not a label.
    /// </para>
    /// </summary>
    internal static bool AdoptDetectedIdentity(Account account, StatementImportBatch batch)
    {
        var learned = false;
        if (string.IsNullOrWhiteSpace(account.MaskedAccountNumber)
            && batch.DetectedAccountMask is { Length: > 0 } mask)
        {
            account.MaskedAccountNumber = mask;
            learned = true;
        }

        if (string.IsNullOrWhiteSpace(account.InstitutionName)
            && batch.DetectedInstitution is { Length: > 0 } institution)
        {
            account.InstitutionName = institution;
            learned = true;
        }

        return learned;
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

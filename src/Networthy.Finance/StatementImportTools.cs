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
/// approved lines become Transactions. Two approval gates on purpose: importing external data
/// starts gated (import_statement), and NOTHING posts until approve_import_batch — reviewing the
/// extracted lines is the product's core "AI drafts, human decides" moment.
/// </summary>
public sealed class StatementImportTools(
    FinanceDbContext db,
    IFileStore files,
    IJobQueue jobs,
    ITenantContext tenant,
    ICurrentUser currentUser)
{
    [Description("Import an uploaded bank statement (CSV/OFX/QFX; the file id comes from the message's attachment block). Extraction runs in the background; review with review_import_batch before anything posts. Side-effecting and requires approval.")]
    public async Task<string> ImportStatement(
        [Description("The stored file id (a GUID) of the uploaded statement.")] string fileId,
        [Description("The account name this statement belongs to.")] string accountName,
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

        var account = await db.Accounts.FirstOrDefaultAsync(
            a => EF.Functions.ILike(a.Name, accountName.Trim()), cancellationToken);
        if (account is null || !account.IsVisibleTo(currentUser.UserId))
        {
            return $"No account named '{accountName}' exists (or it is private to another member). Use list_accounts.";
        }

        var batch = new StatementImportBatch
        {
            TenantId = tenant.RequireTenantId(),
            AccountId = account.Id,
            SourceFileId = stored.Id,
            FileName = stored.FileName,
            Status = "queued",
            CreatedByUserId = currentUser.UserId,
        };
        db.ImportBatches.Add(batch);
        await db.SaveChangesAsync(cancellationToken);

        await jobs.EnqueueAsync(FinanceModule.Id, StatementParseJobHandler.JobKind,
            new StatementParseArgs(batch.Id), cancellationToken);

        return $"Import of '{stored.FileName}' into '{account.Name}' is queued for extraction. " +
               "Once parsed, review the lines with review_import_batch — nothing posts until you approve.";
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

        var sb = new StringBuilder($"Extracted from '{batch.FileName}' ({lines.Count} line(s)) — review, then approve_import_batch:\n");
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
        return sb.ToString();
    }

    [Description("List every import batch that has not been approved yet (file name, when it was imported, status, line count). Read-only — use this to enumerate what's pending when several statements are in flight, then pick one by file name for review_import_batch or approve_import_batch.")]
    public async Task<string> ListImportBatches(CancellationToken cancellationToken = default)
    {
        var visibleAccountIds = db.Accounts
            .Where(a => a.RestrictedToUserId == null || a.RestrictedToUserId == currentUser.UserId)
            .Select(a => a.Id);
        var batches = await db.ImportBatches
            .Where(b => b.Status != "approved" && visibleAccountIds.Contains(b.AccountId))
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
                "failed" => $"failed: {batch.FailureReason}",
                _ => batch.Status,
            };
            sb.AppendLine($"- '{batch.FileName}' · imported {batch.CreatedAt:yyyy-MM-dd HH:mm} UTC · {detail}");
        }

        sb.Append("Pick one by file name with review_import_batch; nothing posts until approve_import_batch.");
        return sb.ToString();
    }

    [Description("Approve a reviewed import batch: its lines post as transactions (with the suggested categories) and the account balance updates. Side-effecting and requires approval.")]
    public async Task<string> ApproveImportBatch(
        [Description("Optional file name (or part of it) to pick a specific batch; defaults to the most recent parsed one.")] string? fileName = null,
        CancellationToken cancellationToken = default)
    {
        var batch = await FindBatchAsync(fileName, cancellationToken);
        if (batch is null)
        {
            return "No import batches yet. Attach a statement and run import_statement.";
        }

        return await ApproveBatchAsync(batch, cancellationToken);
    }

    /// <summary>The approval core, shared by the chat tool (which resolves by file name) and the
    /// review endpoints (which resolve the latest parsed batch, or one by id).</summary>
    internal async Task<string> ApproveBatchAsync(StatementImportBatch batch, CancellationToken cancellationToken)
    {
        if (batch.Status != "parsed")
        {
            return $"'{batch.FileName}' is {batch.Status} — only a parsed batch can be approved.";
        }

        var lines = Deserialize(batch.ExtractedLinesJson);
        if (lines.Count == 0)
        {
            return $"'{batch.FileName}' has no lines to post.";
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

        return $"Posted {lines.Count} transaction(s) from '{batch.FileName}' to '{account.Name}'. " +
               $"New balance: {account.CachedBalance:N2} {account.CurrencyCode}.";
    }

    private async Task<StatementImportBatch?> FindBatchAsync(string? fileName, CancellationToken cancellationToken)
    {
        var query = db.ImportBatches.AsQueryable();
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var pattern = $"%{fileName.Trim()}%";
            query = query.Where(b => EF.Functions.ILike(b.FileName, pattern));
        }

        return await query.OrderByDescending(b => b.CreatedAt).FirstOrDefaultAsync(cancellationToken);
    }

    internal static IReadOnlyList<ExtractedLine> Deserialize(string? json) =>
        json is null
            ? []
            : JsonSerializer.Deserialize<List<ExtractedLine>>(json, JsonSerializerOptions.Web) ?? [];
}

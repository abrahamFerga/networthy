using System.Text;
using System.Text.Json;
using Plenipo.Application.Files;
using Plenipo.Application.Jobs;
using Plenipo.Application.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Networthy.Finance.Persistence;

namespace Networthy.Finance;

/// <summary>Arguments for a <c>finance.statement-parse</c> job.</summary>
public sealed record StatementParseArgs(Guid BatchId);

/// <summary>
/// Extracts an uploaded statement into reviewable lines, on the platform job primitive (retries
/// and progress for free). ADR-0004's hybrid order: deterministic template extractors first
/// (CSV, then OFX/QFX), the AI leg (<see cref="IStatementAiExtractor"/>) as fallback for
/// anything they can't read. Failure is a first-class outcome with an honest reason — never a
/// silently empty batch.
/// </summary>
public sealed class StatementParseJobHandler : IJobHandler
{
    public const string JobKind = "finance.statement-parse";

    /// <summary>Mutable per-member stream for import outcomes (see FinanceModule.NotificationCategories).</summary>
    public const string NotificationCategory = "finance.imports";

    public string Kind => JobKind;

    public async Task<string?> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        var args = JsonSerializer.Deserialize<StatementParseArgs>(context.ArgumentsJson, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException("Statement parse arguments are missing.");

        var services = context.ScopedServices;
        var db = services.GetRequiredService<FinanceDbContext>();
        var files = services.GetRequiredService<IFileStore>();
        var aiExtractor = services.GetRequiredService<IStatementAiExtractor>();
        var notifier = services.GetRequiredService<INotifier>();
        // Same scope as the extractor and the OCR router — this instance IS their evidence trail.
        var ocr = services.GetRequiredService<OcrDiagnostics>();

        var batch = await db.ImportBatches.FirstOrDefaultAsync(b => b.Id == args.BatchId, cancellationToken)
            ?? throw new InvalidOperationException($"Import batch {args.BatchId} does not exist.");

        await context.ReportProgressAsync(10, "reading the uploaded file", cancellationToken);
        await using var stream = await files.OpenReadAsync(batch.SourceFileId, cancellationToken);
        if (stream is null)
        {
            batch.Status = "failed";
            batch.FailureReason = "The uploaded file is no longer available.";
            await db.SaveChangesAsync(cancellationToken);
            await NotifyOutcomeAsync(notifier, context.TenantId, batch, cancellationToken);
            return null;
        }

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        var text = Encoding.UTF8.GetString(bytes);

        var categories = await db.Categories.Select(c => c.Name).ToListAsync(cancellationToken);

        await context.ReportProgressAsync(40, "extracting line items", cancellationToken);
        // Template legs first (deterministic), AI leg as fallback (ADR-0004). Whichever leg
        // reads the lines also reads the account hint — same text, same pass.
        IReadOnlyList<ExtractedLine>? lines;
        DetectedAccountHint? hint;
        var templateLines = StatementExtraction.TryExtractCsv(text, categories)
            ?? StatementExtraction.TryExtractOfx(text, categories);
        if (templateLines is not null)
        {
            lines = templateLines;
            hint = StatementExtraction.DetectAccountHint(text);
        }
        else
        {
            var documentResult = await aiExtractor.ExtractAsync(
                batch.SourceFileId, batch.FileName, bytes, categories, cancellationToken);
            lines = documentResult?.Lines;
            hint = documentResult?.AccountHint;
        }

        if (lines is null || lines.Count == 0)
        {
            batch.Status = "failed";
            batch.FailureReason = ComposeFailureReason(ocr);
            await db.SaveChangesAsync(cancellationToken);
            await NotifyOutcomeAsync(notifier, context.TenantId, batch, cancellationToken);
            return null;
        }

        batch.ExtractedLinesJson = JsonSerializer.Serialize(lines, JsonSerializerOptions.Web);
        batch.LinesFrom = lines.Min(l => l.Date);
        batch.LinesTo = lines.Max(l => l.Date);
        batch.DetectedInstitution = hint?.Institution;
        batch.DetectedAccountMask = hint?.MaskLast4 is { } last4 ? $"••••{last4}" : null;
        batch.DetectedCurrency = hint?.Currency;

        // Imported without an account: try to recognize it before asking. Only accounts the
        // importer can see are candidates, and only an UNAMBIGUOUS match auto-assigns — a
        // wrong guess posts money to the wrong ledger, so ambiguity honestly asks instead.
        if (batch.AccountId is null)
        {
            var candidates = await db.Accounts
                .Where(a => a.RestrictedToUserId == null || a.RestrictedToUserId == batch.CreatedByUserId)
                .ToListAsync(cancellationToken);
            var match = StatementImportTools.MatchAccount(candidates, hint);
            if (match is not null)
            {
                batch.AccountId = match.Id;
            }
        }

        batch.Status = batch.AccountId is null ? "needs-account" : "parsed";
        await db.SaveChangesAsync(cancellationToken);
        await NotifyOutcomeAsync(notifier, context.TenantId, batch, cancellationToken);

        var outcome = batch.Status == "needs-account"
            ? $"{lines.Count} line(s) extracted — needs an account before review"
            : $"{lines.Count} line(s) extracted, awaiting review";
        await context.ReportProgressAsync(100, outcome, cancellationToken);
        return JsonSerializer.Serialize(new { batch.Id, Lines = lines.Count, batch.Status }, JsonSerializerOptions.Web);
    }

    /// <summary>
    /// An honest, actionable failure reason from the extraction evidence — each state names ITS
    /// fix. The distinctions matter: an OCR server that is enabled but down must say "start it",
    /// not "enable an engine" (the engine WAS enabled); text that parsed to zero lines is a
    /// layout gap, not an OCR problem, and the fix is a CSV/OFX export.
    /// </summary>
    internal static string ComposeFailureReason(OcrDiagnostics ocr)
    {
        if (ocr.DocumentTextChars > 0)
        {
            return $"The file's text was read ({ocr.DocumentTextChars:N0} characters), but no transaction " +
                   "lines were recognized in it. If this is a bank statement, its layout isn't one the " +
                   "parser knows yet — export the same period as CSV or OFX/QFX from your bank and " +
                   "import that instead.";
        }

        if (ocr.EngineFailure is { } failure)
        {
            return $"This looks like a scanned (image-only) PDF, and {failure}. The OCR engine is " +
                   "configured under Admin → Integrations (Admin → Document scanning shows which one is " +
                   "active) — make sure it is running and reachable, then import this file again.";
        }

        if (ocr.EngineConfigured && ocr.EngineReadNoText)
        {
            return "This looks like a scanned (image-only) PDF. The household's OCR engine processed it " +
                   "but found no readable text — try a sharper scan or photo of the statement, or export " +
                   "the same period as CSV or OFX/QFX from your bank instead.";
        }

        return "No extractor could read this file. CSV and OFX/QFX parse directly; PDF statements " +
               "parse through the platform document reader. Scanned (photographed) PDFs additionally " +
               "need OCR — a household admin can enable an engine under Admin → Integrations " +
               "(self-hosted Apache Tika, or Azure Document Intelligence); Admin → Document scanning " +
               "shows which one is active. This file produced no readable transaction lines.";
    }

    /// <summary>
    /// The job's outcome, pushed to the importer's notification bell. Chat usually reports the
    /// outcome inline (import_statement waits briefly), so quick successes stay quiet here —
    /// but a FAILURE always rings (it needs action even if chat was closed), and any outcome
    /// slower than the chat wait rings too, because nothing else will ever surface it.
    /// </summary>
    private static async Task NotifyOutcomeAsync(
        INotifier notifier, Guid tenantId, StatementImportBatch batch, CancellationToken cancellationToken)
    {
        if (batch.CreatedByUserId is not { } userId)
        {
            return;
        }

        var chatAlreadySawIt =
            DateTimeOffset.UtcNow - batch.CreatedAt < StatementImportTools.ExtractionWaitBudget;

        var notification = batch.Status switch
        {
            "failed" => new Notification(
                tenantId, userId, Category: NotificationCategory,
                Title: $"'{batch.FileName}' could not be extracted",
                Body: batch.FailureReason ?? "Extraction failed.",
                Link: "/finance/review"),
            "needs-account" when !chatAlreadySawIt => new Notification(
                tenantId, userId, Category: NotificationCategory,
                Title: $"'{batch.FileName}' needs an account",
                Body: $"The statement (it looks like {StatementImportTools.DetectedLabel(batch)}) is " +
                      "extracted and waiting for you to pick or create the account it belongs to.",
                Link: "/finance/review"),
            "parsed" when !chatAlreadySawIt => new Notification(
                tenantId, userId, Category: NotificationCategory,
                Title: $"'{batch.FileName}' is ready to review",
                Body: "Extraction finished. Review the lines and approve — nothing posts until you do.",
                Link: "/finance/review"),
            _ => null,
        };

        if (notification is not null)
        {
            await notifier.NotifyAsync(notification, cancellationToken);
        }
    }
}

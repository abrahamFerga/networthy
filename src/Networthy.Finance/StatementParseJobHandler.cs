using System.Text;
using System.Text.Json;
using Plenipo.Application.Files;
using Plenipo.Application.Jobs;
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

    public string Kind => JobKind;

    public async Task<string?> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        var args = JsonSerializer.Deserialize<StatementParseArgs>(context.ArgumentsJson, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException("Statement parse arguments are missing.");

        var services = context.ScopedServices;
        var db = services.GetRequiredService<FinanceDbContext>();
        var files = services.GetRequiredService<IFileStore>();
        var aiExtractor = services.GetRequiredService<IStatementAiExtractor>();

        var batch = await db.ImportBatches.FirstOrDefaultAsync(b => b.Id == args.BatchId, cancellationToken)
            ?? throw new InvalidOperationException($"Import batch {args.BatchId} does not exist.");

        await context.ReportProgressAsync(10, "reading the uploaded file", cancellationToken);
        await using var stream = await files.OpenReadAsync(batch.SourceFileId, cancellationToken);
        if (stream is null)
        {
            batch.Status = "failed";
            batch.FailureReason = "The uploaded file is no longer available.";
            await db.SaveChangesAsync(cancellationToken);
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
            batch.FailureReason =
                "No extractor could read this file. CSV and OFX/QFX parse directly; PDF statements " +
                "parse through the platform document reader. Scanned (photographed) PDFs additionally " +
                "need OCR — a household admin can enable an engine under Admin → Integrations " +
                "(self-hosted Apache Tika, or Azure Document Intelligence); Admin → Document scanning " +
                "shows which one is active. This file produced no readable transaction lines.";
            await db.SaveChangesAsync(cancellationToken);
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

        var outcome = batch.Status == "needs-account"
            ? $"{lines.Count} line(s) extracted — needs an account before review"
            : $"{lines.Count} line(s) extracted, awaiting review";
        await context.ReportProgressAsync(100, outcome, cancellationToken);
        return JsonSerializer.Serialize(new { batch.Id, Lines = lines.Count, batch.Status }, JsonSerializerOptions.Web);
    }
}

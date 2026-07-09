using System.Text;
using System.Text.Json;
using Cortex.Application.Files;
using Cortex.Application.Jobs;
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
        // Template legs first (deterministic), AI leg as fallback (ADR-0004).
        var lines = StatementExtraction.TryExtractCsv(text, categories)
            ?? StatementExtraction.TryExtractOfx(text, categories)
            ?? await aiExtractor.ExtractAsync(batch.SourceFileId, batch.FileName, bytes, categories, cancellationToken);

        if (lines is null || lines.Count == 0)
        {
            batch.Status = "failed";
            batch.FailureReason =
                "No extractor could read this file. CSV and OFX/QFX parse directly; PDF statements " +
                "parse through the platform document reader (scanned PDFs additionally need the " +
                "platform OCR capability, e.g. Azure Document Intelligence, configured). This file " +
                "produced no readable transaction lines.";
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }

        batch.ExtractedLinesJson = JsonSerializer.Serialize(lines, JsonSerializerOptions.Web);
        batch.Status = "parsed";
        await db.SaveChangesAsync(cancellationToken);

        await context.ReportProgressAsync(100, $"{lines.Count} line(s) extracted, awaiting review", cancellationToken);
        return JsonSerializer.Serialize(new { batch.Id, Lines = lines.Count }, JsonSerializerOptions.Web);
    }
}

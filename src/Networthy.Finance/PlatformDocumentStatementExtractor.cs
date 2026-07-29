using Plenipo.Application.Documents;

namespace Networthy.Finance;

/// <summary>
/// The DETERMINISTIC-ONLY document leg: <see cref="IDocumentReader"/> extracts the file's text
/// (digital PDFs keylessly; scanned ones through the configured OCR capability), then the
/// deterministic line parsers run. The default registration is <see cref="ModelStatementExtractor"/>,
/// which puts the household's model in front of these same parsers — register THIS type instead
/// to opt a deployment out of model extraction entirely. The human review gate catches whatever
/// a layout heuristic gets wrong — that's what the review step is FOR.
/// </summary>
public sealed class PlatformDocumentStatementExtractor(
    IDocumentReader reader, OcrDiagnostics diagnostics) : IStatementAiExtractor
{
    public async Task<StatementExtractionResult?> ExtractAsync(
        Guid fileId, string fileName, byte[] content, IReadOnlyList<string> categories,
        CancellationToken cancellationToken = default)
    {
        var text = await reader.ExtractTextAsync(fileId, cancellationToken);
        diagnostics.DocumentTextChars = string.IsNullOrWhiteSpace(text) ? 0 : text.Length;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var lines = StatementExtraction.TryExtractText(text, categories);
        // The account hint rides along with the SAME text the lines came from — the PDF header
        // is where "FIRST EXAMPLE BANK … ending in 4321" lives.
        return lines is null ? null : new StatementExtractionResult(lines, StatementExtraction.DetectAccountHint(text));
    }
}

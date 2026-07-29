using Plenipo.Application.Documents;

namespace Networthy.Finance;

/// <summary>
/// The document leg of ADR-0004's hybrid extraction, built on the platform's document
/// intelligence seams instead of a bespoke integration: <see cref="IDocumentReader"/> extracts
/// the file's text — digital PDFs work with zero configuration, and when the deployment
/// configures the platform's OCR capability (Azure Document Intelligence, per workflow.json's
/// <c>ocr</c> capability) scanned statements work through the exact same call. The text then
/// runs through the deterministic line parser, and the human review gate catches whatever a
/// layout heuristic gets wrong — that's what the review step is FOR.
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

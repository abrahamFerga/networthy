using Cortex.Application.Documents;

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
public sealed class PlatformDocumentStatementExtractor(IDocumentReader reader) : IStatementAiExtractor
{
    public async Task<IReadOnlyList<ExtractedLine>?> ExtractAsync(
        Guid fileId, string fileName, byte[] content, IReadOnlyList<string> categories,
        CancellationToken cancellationToken = default)
    {
        var text = await reader.ExtractTextAsync(fileId, cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return StatementExtraction.TryExtractText(text, categories);
    }
}

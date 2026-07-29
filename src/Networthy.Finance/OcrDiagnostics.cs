namespace Networthy.Finance;

/// <summary>
/// The evidence trail of ONE document-extraction attempt, scoped alongside the extraction
/// pipeline: <see cref="OcrEngineRouter"/> records what OCR was configured and how it fared,
/// <see cref="PlatformDocumentStatementExtractor"/> records how much text the document yielded,
/// and <see cref="StatementParseJobHandler"/> reads it all to compose an honest failure reason.
/// Exists because the alternatives lied: with a single generic message, an OCR server that is
/// ENABLED but unreachable told the user to "enable an engine" they had already enabled
/// (a real incident — Tika configured at localhost:9998, container not running).
/// </summary>
public sealed class OcrDiagnostics
{
    /// <summary>An OCR source was configured (connector enabled with a usable config, or deployment Ocr set).</summary>
    public bool EngineConfigured { get; set; }

    /// <summary>First configured OCR source that failed, as a human sentence ("Self-hosted Tika OCR (http://…) did not respond: …").</summary>
    public string? EngineFailure { get; set; }

    /// <summary>An OCR engine processed the document successfully but found no text at all.</summary>
    public bool EngineReadNoText { get; set; }

    /// <summary>Characters of usable text the document reader ultimately produced (0 = none).</summary>
    public int DocumentTextChars { get; set; }
}

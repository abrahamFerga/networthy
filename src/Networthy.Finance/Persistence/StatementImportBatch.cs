using Cortex.Core.Entities;

namespace Networthy.Finance.Persistence;

/// <summary>
/// One uploaded statement working its way to becoming transactions: queued → parsed (lines
/// extracted, waiting for human review) → approved (lines posted) — or failed, with the reason.
/// Nothing posts until a human approves the batch (SPEC: statement lines are held for review;
/// the approval gate applies to the import exactly like any other AI-assisted write).
/// </summary>
public sealed class StatementImportBatch : TenantEntityBase
{
    public Guid AccountId { get; set; }

    /// <summary>The uploaded file in the platform file store.</summary>
    public Guid SourceFileId { get; set; }

    public required string FileName { get; set; }

    /// <summary>queued | parsed | approved | failed.</summary>
    public required string Status { get; set; }

    /// <summary>The extracted lines awaiting review, as JSON (see ExtractedLine).</summary>
    public string? ExtractedLinesJson { get; set; }

    public string? FailureReason { get; set; }

    public Guid? ReviewedByUserId { get; set; }

    public DateTimeOffset? ReviewedAt { get; set; }

    public Guid? CreatedByUserId { get; set; }
}

using Plenipo.Core.Entities;

namespace Networthy.Finance.Persistence;

/// <summary>
/// One uploaded statement working its way to becoming transactions: queued → parsed (lines
/// extracted, waiting for human review) → approved (lines posted) — or failed, with the reason.
/// A statement imported WITHOUT naming an account detours through needs-account: the parse job
/// records what the document says about itself (institution, masked number) and either matches
/// an existing account or waits for the human to pick/create one. Nothing posts until a human
/// approves the batch (SPEC: statement lines are held for review; the approval gate applies to
/// the import exactly like any other AI-assisted write).
/// </summary>
public sealed class StatementImportBatch : TenantEntityBase
{
    /// <summary>Null while the batch is needs-account — assigned by matching or by the human.</summary>
    public Guid? AccountId { get; set; }

    /// <summary>The uploaded file in the platform file store.</summary>
    public Guid SourceFileId { get; set; }

    public required string FileName { get; set; }

    /// <summary>queued | parsed | needs-account | approved | failed.</summary>
    public required string Status { get; set; }

    /// <summary>The extracted lines awaiting review, as JSON (see ExtractedLine).</summary>
    public string? ExtractedLinesJson { get; set; }

    /// <summary>What the statement says about its own account (DetectedAccountHint), if anything.</summary>
    public string? DetectedInstitution { get; set; }

    /// <summary>Display mask from detection, e.g. "••••4321".</summary>
    public string? DetectedAccountMask { get; set; }

    /// <summary>Dominant ISO currency the statement's amounts are tagged with ("USD"), if one is.</summary>
    public string? DetectedCurrency { get; set; }

    /// <summary>Earliest/latest line dates — the duplicate-period warning compares these.</summary>
    public DateOnly? LinesFrom { get; set; }

    public DateOnly? LinesTo { get; set; }

    public string? FailureReason { get; set; }

    public Guid? ReviewedByUserId { get; set; }

    public DateTimeOffset? ReviewedAt { get; set; }

    public Guid? CreatedByUserId { get; set; }
}

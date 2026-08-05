namespace Networthy.Finance;

/// <summary>
/// A write tool's refusal: a precondition failed, so the requested change did <em>not</em> happen.
///
/// <para>
/// Networthy's write tools used to report refusals by <em>returning</em> a sentence. The platform's
/// <c>ApprovalExecutor</c> classifies an approved re-execution by exception — any non-throwing return
/// is a success — so a returned refusal was recorded as <c>Executed</c> with <c>Error = null</c>,
/// announced in the transcript as "'set_budget' ran", and disclosed on
/// <c>/api/platform/ai-decisions</c> as an approved action carrying no error. A human who approved a
/// change to household money was told it had happened while nothing was written (issue #182).
/// </para>
///
/// <para>
/// Throwing is what makes the refusal legible to the platform, and the platform then carries the
/// message the tool wrote — VERBATIM, never a stack trace — into every surface that matters: the 422
/// response body, <c>PendingApproval.Error</c>, the transcript note ("⚠️ Approved by X, but
/// 'set_budget' failed: &lt;this message&gt;"), and the disclosure row's <c>error</c>. The guidance a
/// refusal carries is therefore not lost by throwing; it <em>is</em> the payload, which is why the
/// message must stay written for a person to read.
/// </para>
///
/// <para>
/// Refusal means a PRECONDITION failed — unknown category, unknown account, invalid argument, wrong
/// state, not permitted. It does not mean "the action ran and there was nothing to do": an empty but
/// valid outcome (a re-import with no new lines, a sweep that found no candidate pairs) is a genuine
/// success and keeps returning prose. Blurring that line would relabel working behaviour as failure,
/// which is the opposite defect and just as dishonest.
/// </para>
/// </summary>
public sealed class ToolRefusalException(string message) : Exception(message);

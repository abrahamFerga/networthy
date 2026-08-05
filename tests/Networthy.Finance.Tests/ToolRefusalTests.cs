using System.ComponentModel;
using Microsoft.Extensions.AI;
using Xunit;

namespace Networthy.Finance.Tests;

/// <summary>
/// The load-bearing contract behind issue #182's fix.
///
/// <para>
/// The platform's <c>ApprovalExecutor</c> re-executes an approved tool call as
/// <c>await tool.Function.InvokeAsync(args, ct)</c> and classifies the outcome by exception: a
/// non-throwing return is <c>Executed</c>, a throw becomes <c>Failed</c> with <c>ex.Message</c> as
/// the recorded error. Our refusals therefore have to (a) throw, and (b) arrive at that catch with
/// the tool's own sentence still in <c>Message</c>.
/// </para>
///
/// <para>
/// (b) is the part that is not obvious and not ours to control: a tool is exposed as an
/// <see cref="AIFunction"/> built by <c>AIFunctionFactory.Create</c>, and a reflection-based
/// invoker is entitled to wrap whatever the method throws in a <c>TargetInvocationException</c>
/// ("Exception has been thrown by the target of an invocation."). If it did, every refusal would
/// still be recorded as a failure — correct — but the helpful guidance would be replaced by
/// framework boilerplate, trading one defect for another. This test pins the behaviour we rely on
/// against the pinned Microsoft.Extensions.AI, so a package bump that changes it fails here rather
/// than silently degrading every refusal message in the product.
/// </para>
/// </summary>
public sealed class ToolRefusalTests
{
    private const string Guidance = "No category named 'set budget' exists. Check the Categories tab first.";

    private sealed class Refuser
    {
        [Description("A stand-in for a gated write tool that refuses.")]
        public Task<string> Refuse(
            [Description("The category name.")] string category,
            CancellationToken cancellationToken = default) =>
            throw new ToolRefusalException($"No category named '{category}' exists. Check the Categories tab first.");

        [Description("A stand-in for a gated write tool that succeeds.")]
        public Task<string> Succeed(
            [Description("The category name.")] string category,
            CancellationToken cancellationToken = default) =>
            Task.FromResult($"Budget for {category} set.");
    }

    /// <summary>
    /// The exact shape ApprovalExecutor sees: the refusal surfaces as an exception whose Message is
    /// the tool's own sentence, so the platform's Failed path records guidance rather than a stack
    /// trace. Asserted on the base Exception (not our type) because that is all the executor's
    /// <c>catch (Exception ex)</c> knows about.
    /// </summary>
    [Fact]
    public async Task RefusalMessage_SurvivesTheAIFunctionBoundary_Verbatim()
    {
        var function = AIFunctionFactory.Create(new Refuser().Refuse, name: "set_budget");

        var thrown = await Assert.ThrowsAnyAsync<Exception>(
            async () => await function.InvokeAsync(new AIFunctionArguments { ["category"] = "set budget" }));

        // The message the executor would persist as PendingApproval.Error and announce in the
        // transcript. Equality, not Contains: a wrapped exception would still "contain" nothing of
        // this, but a future partial mangling (prefixing, truncating) must fail here too.
        Assert.Equal(Guidance, thrown.Message);
        Assert.IsType<ToolRefusalException>(thrown);
    }

    /// <summary>
    /// The other half of the contract, so the fix cannot degenerate into "call everything a
    /// failure": a tool that does its work still returns its sentence normally, which the executor
    /// records as Executed.
    /// </summary>
    [Fact]
    public async Task SuccessfulTool_StillReturnsItsProse_AndDoesNotThrow()
    {
        var function = AIFunctionFactory.Create(new Refuser().Succeed, name: "set_budget");

        var result = await function.InvokeAsync(new AIFunctionArguments { ["category"] = "Groceries" });

        Assert.Contains("Budget for Groceries set.", result?.ToString());
    }
}

using System.Net.Http.Json;
using Networthy.Finance;
using Xunit;

namespace Networthy.IntegrationTests.Evals;

/// <summary>
/// Every chat starter chip (issue #134) driven through the real pipeline — auth, RBAC tool
/// filtering, the approval gate, audit, the keyless Mock provider — exactly as clicking it does:
/// the shell sends the chip's text verbatim as the user's message. Data-driven from the product's
/// own list, so a starter added to <c>FinanceCatalog.StarterPrompts</c> is proven the moment it
/// ships, and a reworded one that stops reaching its tool goes red here.
/// <para>
/// The two things a one-click starter must never do: reach nothing (the Mock's fallback reply
/// hands the household all 38 tool names) or ambush a first-time visitor with an approval prompt
/// they have no context for. The approval assertion is empirical on purpose — the gate is the
/// UNION of the ToolDescriptor and ModuleTool flags, so reading either one in a unit test would
/// prove nothing.
/// </para>
/// </summary>
[Collection("api")]
public sealed class StarterPromptRoutingTests(IntegrationFixture fixture)
{
    public static TheoryData<string, string> Starters()
    {
        var data = new TheoryData<string, string>();
        foreach (var starter in FinanceCatalog.StarterPrompts)
        {
            data.Add(starter.Prompt, starter.Tool);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Starters))]
    public async Task Starter_ReachesItsOwnTool_WithoutGatingTheFirstClick(string prompt, string tool)
    {
        using var client = fixture.AdminClient();

        using var response = await client.PostAsJsonAsync("/api/agui/finance",
            new { messages = new[] { new { id = "m1", role = "user", content = prompt } } });
        response.EnsureSuccessStatusCode();
        var run = EvalRun.Parse(await response.Content.ReadAsStringAsync());

        Assert.Contains("RUN_FINISHED", run.EventTypes);
        Assert.DoesNotContain("RUN_ERROR", run.EventTypes);

        // One tool, and the one the chip was written to reach — not a near-miss neighbour
        // (a starter that drifts onto set_budget instead of get_budget_status turns a curious
        // click into a write request).
        Assert.Equal(tool, Assert.Single(run.ToolCalls));
        Assert.DoesNotContain("approval_required", run.CustomEvents);
    }
}

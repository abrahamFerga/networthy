using Networthy.Finance;

namespace Networthy.Finance.Tests;

/// <summary>
/// Foundations guards: the manifest is coherent (id, tabs, permissions) and the starter
/// taxonomy every household seeds from stays sane. The pinned tool list grows feature by
/// feature — each feature adds its tools here and asserts their approval gating.
/// </summary>
public sealed class FinanceCatalogTests
{
    [Fact]
    public void Manifest_DeclaresTheFinanceModule()
    {
        var manifest = new FinanceModule().Manifest;

        Assert.Equal("finance", manifest.Id);
        Assert.Equal("Finance", manifest.DisplayName);
        Assert.Contains(manifest.Tabs, t => t.Id == "chat");
        Assert.Contains(manifest.Tabs, t => t.Id == "categories" && t.Editor is not null);

        // The categories editor is admin-gated; viewing is household-wide.
        var categories = manifest.Tabs.Single(t => t.Id == "categories");
        Assert.Equal(FinanceModule.ViewFinance, categories.Permission);
        Assert.Equal(FinanceModule.ManageCategories, categories.Editor!.Permission);
    }

    [Fact]
    public void Manifest_ToolList_IsPinned()
    {
        var manifest = new FinanceModule().Manifest;

        Assert.Equal(
            ["create_account", "list_accounts", "get_net_worth", "log_own_transaction",
             "categorize_transaction", "edit_transaction", "search_transactions", "summarize_spending",
             "can_i_afford", "list_pending_approvals", "get_activity_log", "set_budget", "get_budget_status", "set_account_visibility", "import_statement", "assign_import_account", "review_import_batch", "approve_import_batch", "discard_import_batch", "set_goal", "contribute_to_goal", "list_goals", "update_account_terms", "get_financial_health", "set_income_source", "list_income_sources", "get_goal_plan", "list_recurring", "get_household_settings", "update_household_settings", "set_exchange_rate", "export_transactions", "generate_monthly_report", "export_activity_log", "list_import_batches", "suggest_transfer_links", "link_transfers", "unlink_transfer"],
            manifest.Tools.Select(t => t.Name));

        // Record-changing tools are approval-gated (ADR-0002); reads are not.
        Assert.All(
            manifest.Tools.Where(t => t.Name is "create_account" or "categorize_transaction" or "edit_transaction"
                or "assign_import_account" or "discard_import_batch" or "approve_import_batch" or "set_account_visibility" or "set_budget" or "set_goal" or "contribute_to_goal" or "update_account_terms" or "set_income_source" or "update_household_settings" or "set_exchange_rate" or "link_transfers" or "unlink_transfer"),
            t => Assert.True(t.RequiresApproval));
        // TWO deliberately ungated writes: log_own_transaction (ADR-0005) and import_statement
        // (ADR-0002 amendment — importing only queues extraction into the review pipeline; the
        // batch approval is the gate, and discard is the one-call undo). Reads are never gated.
        Assert.All(
            manifest.Tools.Where(t => t.Name is "list_accounts" or "get_net_worth" or "log_own_transaction"
                or "import_statement" or "search_transactions" or "summarize_spending" or "can_i_afford" or "review_import_batch"
                or "get_budget_status" or "list_pending_approvals" or "get_activity_log" or "list_import_batches"
                or "suggest_transfer_links"),
            t => Assert.False(t.RequiresApproval));
    }

    [Fact]
    public void ChatTab_OpensWithStartersAndANudge()
    {
        var manifest = new FinanceModule().Manifest;

        // Issue #134: Chat used to open on a bare pane — one line of grey fallback text and
        // nothing else. The shell renders SuggestedPrompts as one-click chips, so an empty set
        // IS the bare pane; this goes red the moment the manifest stops declaring them.
        Assert.NotEmpty(manifest.SuggestedPrompts);
        Assert.Equal(FinanceCatalog.StarterPrompts.Select(p => p.Prompt), manifest.SuggestedPrompts);

        // Chat is the product's primary interface; it carries the same actionable nudge every
        // other tab does, and never a tool name (the standing rule in AgentInstructions).
        var chat = manifest.Tabs.Single(t => t.Id == "chat");
        Assert.False(string.IsNullOrWhiteSpace(chat.Placeholder));
    }

    [Fact]
    public void StarterPrompts_ReachRegisteredTools_AcrossTheAssistantsRange()
    {
        var manifest = new FinanceModule().Manifest;
        var registered = manifest.Tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        // A chip that reaches nothing is worse than no chip. A tool is only callable when it is in
        // BOTH the manifest and FinanceToolSource, and the pinned list above is what the source
        // registers — so membership here is registration.
        Assert.All(FinanceCatalog.StarterPrompts, p => Assert.Contains(p.Tool, registered));

        // The set spans the assistant's range rather than just spending: a read, an affordability
        // verdict, recording something, and a statement import.
        Assert.Contains(FinanceCatalog.StarterPrompts, p => p.Tool == "get_net_worth");
        Assert.Contains(FinanceCatalog.StarterPrompts, p => p.Tool == "can_i_afford");
        Assert.Contains(FinanceCatalog.StarterPrompts, p => p.Tool == "log_own_transaction");
        Assert.Contains(FinanceCatalog.StarterPrompts, p => p.Tool == "import_statement");

        // Copy the household reads never contains the plumbing's names.
        Assert.All(
            FinanceCatalog.StarterPrompts,
            p => Assert.DoesNotContain(registered, name => p.Prompt.Contains(name, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// The keyless <c>Mock</c> provider is what every fresh install, demo, and CI run meets, and it
    /// picks a tool by name-token match — so a starter's WORDING decides whether clicking the chip
    /// does anything. Reworded copy that no longer wins its own tool falls through to the Mock's
    /// canned reply, which hands the household a list of all 38 tool names.
    /// <para>
    /// This mirrors <c>MockChatClient.SelectTool</c> (significant name tokens of 4+ characters
    /// contained in the lowercased message; highest count wins; a tie goes to the first-declared
    /// tool, since the model sees them in declaration order) so the copy stays honest without
    /// Docker. <c>StarterPromptRoutingTests</c> proves the same routes empirically against the real
    /// pipeline — that is the L3 evidence; this is the L1 guard.
    /// </para>
    /// </summary>
    [Fact]
    public void StarterPrompts_WinTheirOwnToolUnderTheKeylessMockProvider()
    {
        var manifest = new FinanceModule().Manifest;
        var toolNames = manifest.Tools.Select(t => t.Name).ToList();

        foreach (var starter in FinanceCatalog.StarterPrompts)
        {
            Assert.Contains(starter.Prompt, manifest.SuggestedPrompts);
            Assert.Equal(starter.Tool, MockRoute(starter.Prompt, toolNames));
        }
    }

    [Fact]
    public void TransactionsTab_ShowsWhereEachRowCameFrom()
    {
        var manifest = new FinanceModule().Manifest;

        // AI-originated rows must be visibly tagged in the ledger the household actually
        // reads (issue #49): the generic tab renders whatever the manifest declares, so the
        // origin column disappearing here would silently remove the disclosure.
        var transactions = manifest.Tabs.Single(t => t.Id == "transactions");
        Assert.Contains(transactions.Columns, c => c.Field == "source");
    }

    [Fact]
    public void StarterCategories_AreUniqueAndNonEmpty()
    {
        Assert.NotEmpty(FinanceCatalog.StarterCategories);
        Assert.Equal(
            FinanceCatalog.StarterCategories.Count,
            FinanceCatalog.StarterCategories.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(FinanceCatalog.StarterCategories, c => Assert.False(string.IsNullOrWhiteSpace(c)));
        // The taxonomy must cover both directions of money.
        Assert.Contains("Salary", FinanceCatalog.StarterCategories);
        Assert.Contains("Groceries", FinanceCatalog.StarterCategories);
    }

    /// <summary>The tool the Mock provider would call for a message, given the tools in the order
    /// the model sees them. Ordinal, lowercased — same comparison the provider makes.</summary>
    private static string? MockRoute(string message, IReadOnlyList<string> toolNames)
    {
        var text = message.ToLowerInvariant();
        string? best = null;
        var bestScore = 0;

        foreach (var name in toolNames)
        {
            var score = name.Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Where(token => token.Length >= 4)
                .Count(token => text.Contains(token, StringComparison.Ordinal));
            if (score > bestScore)
            {
                bestScore = score;
                best = name;
            }
        }

        return best;
    }
}

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
             "can_i_afford", "list_pending_approvals", "get_activity_log", "set_budget", "get_budget_status", "set_account_visibility", "import_statement", "review_import_batch", "approve_import_batch", "set_goal", "contribute_to_goal", "list_goals", "update_account_terms", "get_financial_health", "set_income_source", "list_income_sources", "get_goal_plan", "list_recurring", "get_household_settings", "update_household_settings", "set_exchange_rate", "export_transactions", "generate_monthly_report", "export_activity_log"],
            manifest.Tools.Select(t => t.Name));

        // Record-changing tools are approval-gated (ADR-0002); reads are not.
        Assert.All(
            manifest.Tools.Where(t => t.Name is "create_account" or "categorize_transaction" or "edit_transaction"
                or "import_statement" or "approve_import_batch" or "set_account_visibility" or "set_budget" or "set_goal" or "contribute_to_goal" or "update_account_terms" or "set_income_source" or "update_household_settings" or "set_exchange_rate"),
            t => Assert.True(t.RequiresApproval));
        // log_own_transaction is the module's ONE deliberately ungated write (ADR-0005); reads are never gated.
        Assert.All(
            manifest.Tools.Where(t => t.Name is "list_accounts" or "get_net_worth" or "log_own_transaction"
                or "search_transactions" or "summarize_spending" or "can_i_afford" or "review_import_batch"
                or "get_budget_status" or "list_pending_approvals" or "get_activity_log"),
            t => Assert.False(t.RequiresApproval));
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
}

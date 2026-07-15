using Plenipo.Application.Authorization;
using Plenipo.Modules.Sdk;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Networthy.Finance;

/// <summary>Supplies the Finance module's executable tools. Grows feature by feature; every
/// entry stays permission-gated and record-changing tools stay approval-gated (ADR-0002).</summary>
public sealed class FinanceToolSource : IModuleToolSource
{
    public string ModuleId => FinanceModule.Id;

    // ── Approval-risk tiers (issue #49) ─────────────────────────────────────────────
    // Risk is review-surface ceremony, never a gate: every RequiresApproval tool still
    // blocks for a human either way. Uniform ceremony trains reviewers to rubber-stamp,
    // so a tool is Low ONLY when its action can be undone by one equally-small action
    // and cannot move money, delete records, or change what the household owes/owns.
    // Anything structural, bulk, or ledger-rewriting keeps the High default (nothing
    // downgrades silently). Per-tool decisions, guard-tested in RiskTierAndDisclosureTests:
    //   categorize_transaction  LOW  — retags ONE transaction's category; the exact
    //                                  correction is one more categorize call; balances
    //                                  and the ledger never move.
    //   contribute_to_goal      LOW  — a bookkeeping marker on one unlinked goal, never
    //                                  a transaction; the tool itself accepts negative
    //                                  corrections, so the undo is the same-sized call.
    //   set_exchange_rate       LOW  — upserts the household's own conversion lens for
    //                                  net-worth combination; re-setting the previous
    //                                  rate (kept in the audit diff) restores it exactly.
    //   create_account          HIGH — creates a structural record; changes what the
    //                                  household owns/owes.
    //   edit_transaction        HIGH — rewrites ledger fact (amount moves the account
    //                                  balance), not just a tag.
    //   set_budget              HIGH — a budget change is exactly the consequential
    //                                  write the issue's full review card is for.
    //   set_account_visibility  HIGH — standing privacy configuration: changes which
    //                                  members can see an account.
    //   import_statement        HIGH — admits an external batch into the review
    //                                  pipeline; the start of a bulk operation.
    //   approve_import_batch    HIGH — posts N lines as transactions and moves the
    //                                  balance; bulk by definition.
    //   set_goal                HIGH — creates/updates goal structure (target, date,
    //                                  account link, assumed return).
    //   update_account_terms    HIGH — changes debt terms (APR, minimum): what the
    //                                  household owes per month.
    //   set_income_source       HIGH — standing income configuration; drives goal
    //                                  plans and cash-flow verdicts.
    //   update_household_settings HIGH — currency/time-zone/thresholds recolor every
    //                                  read in the product.
    public IReadOnlyList<ModuleTool> GetTools(IServiceProvider scopedServices)
    {
        var accounts = scopedServices.GetRequiredService<AccountTools>();
        var transactions = scopedServices.GetRequiredService<TransactionTools>();
        var affordability = scopedServices.GetRequiredService<AffordabilityTools>();
        var imports = scopedServices.GetRequiredService<StatementImportTools>();
        var household = scopedServices.GetRequiredService<HouseholdTools>();
        var budgets = scopedServices.GetRequiredService<BudgetTools>();
        var approvals = scopedServices.GetRequiredService<ApprovalSurfaceTools>();
        var goals = scopedServices.GetRequiredService<GoalTools>();
        var health = scopedServices.GetRequiredService<HealthTools>();
        var goalPlans = scopedServices.GetRequiredService<GoalPlanTools>();
        var incomeSources = scopedServices.GetRequiredService<IncomeSourceTools>();
        var recurring = scopedServices.GetRequiredService<RecurringTools>();
        var settings = scopedServices.GetRequiredService<HouseholdSettingsTools>();
        var exports = scopedServices.GetRequiredService<ExportTools>();

        return
        [
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "create_account",
                Permission = Permissions.ForTool(ModuleId, "create_account"),
                Function = AIFunctionFactory.Create(accounts.CreateAccount, name: "create_account"),
                RequiresApproval = true,
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "list_accounts",
                Permission = Permissions.ForTool(ModuleId, "list_accounts"),
                Function = AIFunctionFactory.Create(accounts.ListAccounts, name: "list_accounts"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "get_net_worth",
                Permission = Permissions.ForTool(ModuleId, "get_net_worth"),
                Function = AIFunctionFactory.Create(accounts.GetNetWorth, name: "get_net_worth"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "log_own_transaction",
                Permission = Permissions.ForTool(ModuleId, "log_own_transaction"),
                Function = AIFunctionFactory.Create(transactions.LogOwnTransaction, name: "log_own_transaction"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "categorize_transaction",
                Permission = Permissions.ForTool(ModuleId, "categorize_transaction"),
                Function = AIFunctionFactory.Create(transactions.CategorizeTransaction, name: "categorize_transaction"),
                RequiresApproval = true,
                Risk = ApprovalRisk.Low, // one tag on one row; the undo is one more categorize call

            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "edit_transaction",
                Permission = Permissions.ForTool(ModuleId, "edit_transaction"),
                Function = AIFunctionFactory.Create(transactions.EditTransaction, name: "edit_transaction"),
                RequiresApproval = true,
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "search_transactions",
                Permission = Permissions.ForTool(ModuleId, "search_transactions"),
                Function = AIFunctionFactory.Create(transactions.SearchTransactions, name: "search_transactions"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "summarize_spending",
                Permission = Permissions.ForTool(ModuleId, "summarize_spending"),
                Function = AIFunctionFactory.Create(transactions.SummarizeSpending, name: "summarize_spending"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "can_i_afford",
                Permission = Permissions.ForTool(ModuleId, "can_i_afford"),
                Function = AIFunctionFactory.Create(affordability.CanIAfford, name: "can_i_afford"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "list_pending_approvals",
                Permission = Permissions.ForTool(ModuleId, "list_pending_approvals"),
                Function = AIFunctionFactory.Create(approvals.ListPendingApprovals, name: "list_pending_approvals"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "get_activity_log",
                Permission = Permissions.ForTool(ModuleId, "get_activity_log"),
                Function = AIFunctionFactory.Create(approvals.GetActivityLog, name: "get_activity_log"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "set_budget",
                Permission = Permissions.ForTool(ModuleId, "set_budget"),
                Function = AIFunctionFactory.Create(budgets.SetBudget, name: "set_budget"),
                RequiresApproval = true,
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "get_budget_status",
                Permission = Permissions.ForTool(ModuleId, "get_budget_status"),
                Function = AIFunctionFactory.Create(budgets.GetBudgetStatus, name: "get_budget_status"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "set_account_visibility",
                Permission = Permissions.ForTool(ModuleId, "set_account_visibility"),
                Function = AIFunctionFactory.Create(household.SetAccountVisibility, name: "set_account_visibility"),
                RequiresApproval = true,
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "import_statement",
                Permission = Permissions.ForTool(ModuleId, "import_statement"),
                Function = AIFunctionFactory.Create(imports.ImportStatement, name: "import_statement"),
                RequiresApproval = true,
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "review_import_batch",
                Permission = Permissions.ForTool(ModuleId, "review_import_batch"),
                Function = AIFunctionFactory.Create(imports.ReviewImportBatch, name: "review_import_batch"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "approve_import_batch",
                Permission = Permissions.ForTool(ModuleId, "approve_import_batch"),
                Function = AIFunctionFactory.Create(imports.ApproveImportBatch, name: "approve_import_batch"),
                RequiresApproval = true,
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "set_goal",
                Permission = Permissions.ForTool(ModuleId, "set_goal"),
                Function = AIFunctionFactory.Create(goals.SetGoal, name: "set_goal"),
                RequiresApproval = true,
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "contribute_to_goal",
                Permission = Permissions.ForTool(ModuleId, "contribute_to_goal"),
                Function = AIFunctionFactory.Create(goals.ContributeToGoal, name: "contribute_to_goal"),
                RequiresApproval = true,
                Risk = ApprovalRisk.Low, // a marker, never a transaction; negative corrections are the built-in undo

            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "list_goals",
                Permission = Permissions.ForTool(ModuleId, "list_goals"),
                Function = AIFunctionFactory.Create(goals.ListGoals, name: "list_goals"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "update_account_terms",
                Permission = Permissions.ForTool(ModuleId, "update_account_terms"),
                Function = AIFunctionFactory.Create(accounts.UpdateAccountTerms, name: "update_account_terms"),
                RequiresApproval = true,
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "get_financial_health",
                Permission = Permissions.ForTool(ModuleId, "get_financial_health"),
                Function = AIFunctionFactory.Create(health.GetFinancialHealth, name: "get_financial_health"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "set_income_source",
                Permission = Permissions.ForTool(ModuleId, "set_income_source"),
                Function = AIFunctionFactory.Create(incomeSources.SetIncomeSource, name: "set_income_source"),
                RequiresApproval = true,
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "list_income_sources",
                Permission = Permissions.ForTool(ModuleId, "list_income_sources"),
                Function = AIFunctionFactory.Create(incomeSources.ListIncomeSources, name: "list_income_sources"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "get_goal_plan",
                Permission = Permissions.ForTool(ModuleId, "get_goal_plan"),
                Function = AIFunctionFactory.Create(goalPlans.GetGoalPlan, name: "get_goal_plan"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "list_recurring",
                Permission = Permissions.ForTool(ModuleId, "list_recurring"),
                Function = AIFunctionFactory.Create(recurring.ListRecurring, name: "list_recurring"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "get_household_settings",
                Permission = Permissions.ForTool(ModuleId, "get_household_settings"),
                Function = AIFunctionFactory.Create(settings.GetHouseholdSettings, name: "get_household_settings"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "update_household_settings",
                Permission = Permissions.ForTool(ModuleId, "update_household_settings"),
                Function = AIFunctionFactory.Create(settings.UpdateHouseholdSettings, name: "update_household_settings"),
                RequiresApproval = true,
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "set_exchange_rate",
                Permission = Permissions.ForTool(ModuleId, "set_exchange_rate"),
                Function = AIFunctionFactory.Create(settings.SetExchangeRate, name: "set_exchange_rate"),
                RequiresApproval = true,
                Risk = ApprovalRisk.Low, // a display lens, not money; re-setting the audited old rate restores it exactly

            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "export_transactions",
                Permission = Permissions.ForTool(ModuleId, "export_transactions"),
                Function = AIFunctionFactory.Create(exports.ExportTransactions, name: "export_transactions"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "generate_monthly_report",
                Permission = Permissions.ForTool(ModuleId, "generate_monthly_report"),
                Function = AIFunctionFactory.Create(exports.GenerateMonthlyReport, name: "generate_monthly_report"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "export_activity_log",
                Permission = Permissions.ForTool(ModuleId, "export_activity_log"),
                Function = AIFunctionFactory.Create(exports.ExportActivityLog, name: "export_activity_log"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "list_import_batches",
                Permission = Permissions.ForTool(ModuleId, "list_import_batches"),
                Function = AIFunctionFactory.Create(imports.ListImportBatches, name: "list_import_batches"),
            },
        ];
    }
}

using Cortex.Application.Authorization;
using Cortex.Modules.Sdk;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Networthy.Finance;

/// <summary>Supplies the Finance module's executable tools. Grows feature by feature; every
/// entry stays permission-gated and record-changing tools stay approval-gated (ADR-0002).</summary>
public sealed class FinanceToolSource : IModuleToolSource
{
    public string ModuleId => FinanceModule.Id;

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
        ];
    }
}

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
        ];
    }
}

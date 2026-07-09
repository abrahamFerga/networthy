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
        ];
    }
}

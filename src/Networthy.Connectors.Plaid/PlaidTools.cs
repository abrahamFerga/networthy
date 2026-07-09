using System.ComponentModel;
using System.Text;
using Cortex.Application.Authorization;
using Cortex.Connectors.Sdk;
using Cortex.Core.Identity;
using Cortex.Core.Multitenancy;
using Cortex.Modules.Sdk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Networthy.Finance.Persistence;

namespace Networthy.Connectors.Plaid;

/// <summary>
/// The Plaid connector's tools: see what the linked item exposes, bind a Plaid account to a
/// Networthy account, and sync — synced transactions land in the SAME Transaction entity
/// statement upload produces (Source=plaid), so budgets, summaries, and net worth treat them
/// identically. Linking and syncing are approval-gated: both bring external data in.
/// </summary>
public sealed class PlaidTools(
    IConnectorSettings settings,
    IPlaidClient plaid,
    FinanceDbContext db,
    ITenantContext tenant,
    ICurrentUser currentUser)
{
    private const string NotConfigured =
        "The Plaid connector is not enabled for this tenant (or is missing its credentials). " +
        "A household admin can configure it under Integrations — bank linking is always optional; " +
        "statement upload works without it.";

    [Description("List the accounts the linked Plaid item exposes, with their Networthy mapping status.")]
    public async Task<string> ListPlaidAccounts(CancellationToken cancellationToken = default)
    {
        var connection = await ResolveAsync(cancellationToken);
        if (connection is null)
        {
            return NotConfigured;
        }

        var accounts = await plaid.GetAccountsAsync(connection, cancellationToken);
        if (accounts.Count == 0)
        {
            return "The linked Plaid item exposes no accounts.";
        }

        var links = await db.PlaidLinks.ToListAsync(cancellationToken);
        var networthyNames = await db.Accounts.ToDictionaryAsync(a => a.Id, a => a.Name, cancellationToken);

        var sb = new StringBuilder("Plaid accounts:\n");
        foreach (var account in accounts)
        {
            var link = links.FirstOrDefault(l => l.PlaidAccountId == account.AccountId);
            sb.AppendLine($"- {account.Name} [{account.Subtype ?? "?"}] " +
                          $"{(account.CurrentBalance is { } b ? $"{b:N2} {account.Currency ?? ""}" : "")} — " +
                          (link is not null && networthyNames.TryGetValue(link.AccountId, out var mapped)
                              ? $"linked to '{mapped}'"
                              : "not linked (link_plaid_account)"));
        }

        return sb.ToString();
    }

    [Description("Bind a Plaid account to a Networthy account so sync knows where transactions go (creates the Networthy account if it doesn't exist). Side-effecting and requires approval.")]
    public async Task<string> LinkPlaidAccount(
        [Description("The Plaid account name as shown by list_plaid_accounts.")] string plaidAccountName,
        [Description("The Networthy account name to bind it to (created if absent).")] string networthyAccountName,
        CancellationToken cancellationToken = default)
    {
        var connection = await ResolveAsync(cancellationToken);
        if (connection is null)
        {
            return NotConfigured;
        }

        var accounts = await plaid.GetAccountsAsync(connection, cancellationToken);
        var plaidAccount = accounts.FirstOrDefault(
            a => a.Name.Contains(plaidAccountName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (plaidAccount is null)
        {
            return $"No Plaid account matches '{plaidAccountName}'. Use list_plaid_accounts.";
        }

        var name = networthyAccountName.Trim();
        var account = await db.Accounts.FirstOrDefaultAsync(
            a => EF.Functions.ILike(a.Name, name), cancellationToken);
        if (account is null)
        {
            account = new Account
            {
                TenantId = tenant.RequireTenantId(),
                Name = name,
                Type = PlaidMapping.ToAccountType(plaidAccount.Subtype),
                CurrencyCode = (plaidAccount.Currency ?? "USD").ToUpperInvariant(),
                CachedBalance = plaidAccount.CurrentBalance ?? 0m,
                InstitutionName = "via Plaid",
                CreatedByUserId = currentUser.UserId,
            };
            db.Accounts.Add(account);
        }

        var existing = await db.PlaidLinks.FirstOrDefaultAsync(
            l => l.PlaidAccountId == plaidAccount.AccountId, cancellationToken);
        if (existing is null)
        {
            db.PlaidLinks.Add(new PlaidLinkedAccount
            {
                TenantId = tenant.RequireTenantId(),
                AccountId = account.Id,
                PlaidAccountId = plaidAccount.AccountId,
            });
        }
        else
        {
            existing.AccountId = account.Id;
        }

        await db.SaveChangesAsync(cancellationToken);
        return $"Linked Plaid account '{plaidAccount.Name}' to '{account.Name}'. " +
               "Run sync_plaid_transactions to pull its history.";
    }

    [Description("Pull recent transactions from every linked Plaid account into Networthy (deduplicated; balances update). Side-effecting: imports external data and requires approval.")]
    public async Task<string> SyncPlaidTransactions(
        [Description("How many days back to sync (default 30, max 730).")] int days = 30,
        CancellationToken cancellationToken = default)
    {
        var connection = await ResolveAsync(cancellationToken);
        if (connection is null)
        {
            return NotConfigured;
        }

        var links = await db.PlaidLinks.ToListAsync(cancellationToken);
        if (links.Count == 0)
        {
            return "No Plaid accounts are linked yet. Use list_plaid_accounts, then link_plaid_account.";
        }

        var to = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        var from = to.AddDays(-Math.Clamp(days, 1, 730));
        var transactions = await plaid.GetTransactionsAsync(connection, from, to, cancellationToken);

        var byPlaidAccount = links.ToDictionary(l => l.PlaidAccountId, l => l.AccountId);
        var accounts = await db.Accounts.ToDictionaryAsync(a => a.Id, cancellationToken);

        // Dedupe against what's already on each account in the window (Plaid re-reports history).
        var existingKeys = (await db.Transactions
                .Where(t => t.OccurredOn >= from && t.Source == "plaid")
                .ToListAsync(cancellationToken))
            .GroupBy(t => t.AccountId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(t => PlaidMapping.DedupeKey(t.OccurredOn, t.Amount, t.Description)).ToHashSet());

        var inserted = 0;
        foreach (var txn in transactions)
        {
            if (!byPlaidAccount.TryGetValue(txn.AccountId, out var accountId) ||
                !accounts.TryGetValue(accountId, out var account))
            {
                continue; // an unlinked plaid account — nothing to post to
            }

            var (amount, direction) = PlaidMapping.ToNetworthy(txn.Amount);
            var key = PlaidMapping.DedupeKey(txn.Date, amount, txn.Name);
            var seen = existingKeys.TryGetValue(accountId, out var keys) ? keys : (existingKeys[accountId] = []);
            if (!seen.Add(key))
            {
                continue;
            }

            var transaction = new Transaction
            {
                TenantId = tenant.RequireTenantId(),
                AccountId = accountId,
                OccurredOn = txn.Date,
                Amount = amount,
                CurrencyCode = (txn.Currency ?? account.CurrencyCode).ToUpperInvariant(),
                Description = txn.Name,
                Direction = direction,
                Source = "plaid",
                CreatedByUserId = currentUser.UserId,
            };
            db.Transactions.Add(transaction);
            account.CachedBalance += transaction.BalanceDelta;
            inserted++;
        }

        foreach (var link in links)
        {
            link.LastSyncedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        return $"Synced {from:yyyy-MM-dd} – {to:yyyy-MM-dd}: {inserted} new transaction(s) " +
               $"({transactions.Count - inserted} already present or unlinked).";
    }

    private async Task<PlaidConnection?> ResolveAsync(CancellationToken cancellationToken)
    {
        var values = await settings.GetAsync(PlaidConnector.ConnectorId, cancellationToken);
        if (values is null ||
            !values.TryGetValue("ClientId", out var clientId) || string.IsNullOrWhiteSpace(clientId) ||
            !values.TryGetValue("ClientSecret", out var secret) || string.IsNullOrWhiteSpace(secret) ||
            !values.TryGetValue("AccessToken", out var token) || string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        values.TryGetValue("Environment", out var environment);
        return new PlaidConnection(clientId, secret,
            string.IsNullOrWhiteSpace(environment) ? "sandbox" : environment, token);
    }
}

/// <summary>Supplies the Plaid connector's executable tools.</summary>
public sealed class PlaidToolSource : IConnectorToolSource
{
    public string ConnectorId => PlaidConnector.ConnectorId;

    public IReadOnlyList<ModuleTool> GetTools(IServiceProvider scopedServices)
    {
        var tools = scopedServices.GetRequiredService<PlaidTools>();
        return
        [
            new ModuleTool
            {
                ModuleId = $"connectors.{ConnectorId}",
                Name = "list_plaid_accounts",
                Permission = Permissions.ForConnectorTool(ConnectorId, "list_plaid_accounts"),
                Function = AIFunctionFactory.Create(tools.ListPlaidAccounts, name: "list_plaid_accounts"),
            },
            new ModuleTool
            {
                ModuleId = $"connectors.{ConnectorId}",
                Name = "link_plaid_account",
                Permission = Permissions.ForConnectorTool(ConnectorId, "link_plaid_account"),
                Function = AIFunctionFactory.Create(tools.LinkPlaidAccount, name: "link_plaid_account"),
                RequiresApproval = true,
            },
            new ModuleTool
            {
                ModuleId = $"connectors.{ConnectorId}",
                Name = "sync_plaid_transactions",
                Permission = Permissions.ForConnectorTool(ConnectorId, "sync_plaid_transactions"),
                Function = AIFunctionFactory.Create(tools.SyncPlaidTransactions, name: "sync_plaid_transactions"),
                RequiresApproval = true,
            },
        ];
    }
}

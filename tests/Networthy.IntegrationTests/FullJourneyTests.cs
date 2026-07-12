using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Networthy.Finance;
using Networthy.Finance.Persistence;
using Xunit;

namespace Networthy.IntegrationTests;

/// <summary>
/// The household's whole journey, end to end against real Postgres: accounts → transactions →
/// budgets (with the OVER flag) → affordability → net-worth snapshot + trend → activity log.
/// Tool methods are invoked as the platform's approval pipeline invokes them post-approval;
/// the gate mechanism itself is exercised in ChatAndApprovalTests.
/// </summary>
[Collection("api")]
public sealed class FullJourneyTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task Accounts_Transactions_Budgets_Affordability_ActivityLog()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var services = scope.ServiceProvider;

        // 1. Accounts (the create tool is approval-gated in chat; here it runs as-if-approved).
        var accounts = services.GetRequiredService<AccountTools>();
        Assert.Contains("Created checking account 'Chase Checking'", await accounts.CreateAccount("Chase Checking", "checking", "usd", 2500));
        Assert.Contains("already exists", await accounts.CreateAccount("chase checking", "checking", "USD"));
        Assert.Contains("Visa", await accounts.CreateAccount("Visa", "credit", "USD", -400));

        // 2. Transactions — the ADR-0005 quick-capture write plus reads.
        var transactions = services.GetRequiredService<TransactionTools>();
        Assert.Contains("Logged expense of 82.45", await transactions.LogOwnTransaction("Chase Checking", 82.45, "Whole Foods", category: "Groceries"));
        Assert.Contains("Logged income of 2,500.00", await transactions.LogOwnTransaction("Chase Checking", 2500, "ACME payroll", direction: "income", category: "Salary"));
        var search = await transactions.SearchTransactions(text: "whole");
        Assert.Contains("Whole Foods", search);
        Assert.Contains("[Groceries]", search);
        Assert.Contains("Groceries: 82.45", await transactions.SummarizeSpending());

        // Balance moved deterministically: 2500 - 82.45 + 2500.
        Assert.Contains("4,917.55", await accounts.ListAccounts());

        // 3. Budgets: under, then pushed OVER.
        var budgets = services.GetRequiredService<BudgetTools>();
        Assert.Contains("Budget for Groceries", await budgets.SetBudget("Groceries", 100));
        Assert.Contains("17.55 left", await budgets.GetBudgetStatus());
        await transactions.LogOwnTransaction("Chase Checking", 40, "Trader Joes groceries run", category: "Groceries");
        Assert.Contains("OVER by 22.45", await budgets.GetBudgetStatus());

        // 4. Affordability: computed from liquid (credit excluded), honest verdict.
        var affordability = services.GetRequiredService<AffordabilityTools>();
        var verdict = await affordability.CanIAfford(200, category: "Dining");
        Assert.Contains("Yes", verdict);
        Assert.Contains("liquid", verdict);
        Assert.Contains("No —", await affordability.CanIAfford(1_000_000));

        // 5. Net worth: snapshot sweep writes today's rows; trend reads back.
        var written = await NetWorthSnapshotService.SweepOnceAsync(services);
        Assert.True(written >= 1);
        var netWorth = await accounts.GetNetWorth();
        Assert.Contains("USD", netWorth);

        // 6. The activity log tells the story with sources. The journey's transactions were
        // logged through the chat tool, so they carry the AI-origin tag (issue #49), not
        // "manual" — that word now belongs to the form endpoints alone.
        var activity = await services.GetRequiredService<ApprovalSurfaceTools>().GetActivityLog();
        Assert.Contains("transaction (assistant)", activity);
        Assert.Contains("account created", activity);
        Assert.Contains("budget set", activity);
    }

    [Fact]
    public async Task VisibilityScoping_HidesPrivateAccountsFromOtherMembers()
    {
        var (scope, tenantId, ownerId) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<FinanceDbContext>();

        db.Accounts.Add(new Account
        {
            TenantId = tenantId, Name = "Private stash", Type = "cash", CurrencyCode = "USD",
            CachedBalance = 500, RestrictedToUserId = ownerId,
        });
        db.Accounts.Add(new Account
        {
            TenantId = tenantId, Name = "Shared savings", Type = "savings", CurrencyCode = "USD",
            CachedBalance = 1000,
        });
        await db.SaveChangesAsync();

        // The owner sees both…
        var owned = await services.GetRequiredService<AccountTools>().ListAccounts();
        Assert.Contains("Private stash", owned);
        Assert.Contains("(private)", owned);

        // …another member sees only the shared one.
        var context = services.GetRequiredService<Cortex.Infrastructure.Context.RequestContext>();
        context.SetUser(Guid.NewGuid(), "other-member", "Other Member");
        var theirs = await services.GetRequiredService<AccountTools>().ListAccounts();
        Assert.DoesNotContain("Private stash", theirs);
        Assert.Contains("Shared savings", theirs);
    }
}

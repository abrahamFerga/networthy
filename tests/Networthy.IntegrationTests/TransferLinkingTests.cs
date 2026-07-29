using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Networthy.Finance;
using Networthy.Finance.Persistence;
using Xunit;

namespace Networthy.IntegrationTests;

/// <summary>
/// SPEC v2.1 through the real moving parts: money moved between the household's own accounts is
/// suggested, linked on a human's say-so, excluded from spending math while linked, visible with
/// its counterpart in the ledger reads, reversible — and the statement-approval message itself
/// surfaces the candidates. The collection shares one tenant, so every account and description
/// here is "transfer-probe"-distinctive, links are made ONLY by naming both legs (never the bulk
/// path, which could grab another test's rows), and sums are asserted through a category no other
/// test posts transactions to.
/// </summary>
[Collection("api")]
public sealed class TransferLinkingTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task Suggest_Link_Exclude_Unlink_RoundTrip()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var services = scope.ServiceProvider;
        var accounts = services.GetRequiredService<AccountTools>();
        var transactions = services.GetRequiredService<TransactionTools>();
        var transfers = services.GetRequiredService<TransferTools>();

        await accounts.CreateAccount("Transfer Probe Checking A", "checking", "USD", 5_000);
        await accounts.CreateAccount("Transfer Probe Checking B", "checking", "USD", 100);
        await transactions.LogOwnTransaction(
            "Transfer Probe Checking A", 777.31, "transfer-probe wire out to B", category: "Travel");
        await transactions.LogOwnTransaction(
            "Transfer Probe Checking B", 777.31, "transfer-probe deposit from A", direction: "income");

        // Before anything links, the expense leg counts as Travel spending…
        Assert.Contains("777.31", await transactions.SummarizeSpending(category: "Travel"));

        // …and the matcher already sees the pair (read-only — nothing changed yet).
        var suggested = await transfers.SuggestTransferLinks();
        Assert.Contains("transfer-probe wire out to B", suggested);
        Assert.Contains("transfer-probe deposit from A", suggested);

        var linked = await transfers.LinkTransfers(
            "transfer-probe wire out to B", "transfer-probe deposit from A");
        Assert.StartsWith("Linked as one transfer", linked);

        // Both legs share one pair id, and the linked legs carry no category anymore.
        var db = services.GetRequiredService<FinanceDbContext>();
        var outLeg = await db.Transactions.SingleAsync(t => t.Description == "transfer-probe wire out to B");
        var inLeg = await db.Transactions.SingleAsync(t => t.Description == "transfer-probe deposit from A");
        Assert.NotNull(outLeg.TransferGroupId);
        Assert.Equal(outLeg.TransferGroupId, inLeg.TransferGroupId);
        Assert.Null(outLeg.CategoryId);

        // Linked: gone from spending math (the balance itself already moved when it was logged).
        Assert.DoesNotContain("777.31", await transactions.SummarizeSpending(category: "Travel"));

        // The ledger read shows the row AS a transfer, naming the counterpart account.
        var search = await transactions.SearchTransactions(text: "transfer-probe wire out");
        Assert.Contains("[transfer ⇄ Transfer Probe Checking B]", search);

        // Unlink restores the ordinary reading; the leg is uncategorized until someone retags it.
        Assert.Contains("Unlinked", await transfers.UnlinkTransfer("transfer-probe wire out"));
        Assert.Null((await db.Transactions.SingleAsync(t => t.Id == outLeg.Id)).TransferGroupId);
        await transactions.CategorizeTransaction("transfer-probe wire out to B", "Travel");
        Assert.Contains("777.31", await transactions.SummarizeSpending(category: "Travel"));
    }

    [Fact]
    public async Task ApprovingAStatement_ReportsTransferCandidates_InItsOwnMessage()
    {
        var (scope, tenantId, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<FinanceDbContext>();
        var today = await services.GetRequiredService<HouseholdContext>().TodayAsync();

        await services.GetRequiredService<AccountTools>()
            .CreateAccount("Transfer Probe Hook Source", "checking", "USD", 2_000);
        await services.GetRequiredService<AccountTools>()
            .CreateAccount("Transfer Probe Hook Target", "checking", "USD", 0);
        await services.GetRequiredService<TransactionTools>().LogOwnTransaction(
            "Transfer Probe Hook Source", 654.42, "transfer-probe hook wire out");

        // A parsed batch for the TARGET account whose one line mirrors the source's expense —
        // the same shape a real "other side of the transfer" statement produces.
        var target = await db.Accounts.SingleAsync(a => a.Name == "Transfer Probe Hook Target");
        db.ImportBatches.Add(new StatementImportBatch
        {
            TenantId = tenantId,
            AccountId = target.Id,
            SourceFileId = Guid.NewGuid(),
            FileName = "transfer-probe-hook.csv",
            Status = "parsed",
            ExtractedLinesJson = JsonSerializer.Serialize(
                new[] { new ExtractedLine(today, "transfer-probe hook deposit", 654.42m, "income", null) },
                JsonSerializerOptions.Web),
        });
        await db.SaveChangesAsync();

        var message = await services.GetRequiredService<StatementImportTools>()
            .ApproveImportBatch(fileName: "transfer-probe-hook");

        // One message does both jobs: confirms the post AND surfaces the pocket-to-pocket pair.
        Assert.Contains("Posted 1 transaction(s)", message);
        Assert.Contains("look like transfers between your accounts", message);
        Assert.Contains("transfer-probe hook wire out", message);
        Assert.Contains("transfer-probe hook deposit", message);
        Assert.Contains("Nothing is linked yet", message);
    }

    [Fact]
    public async Task TheLedger_ScopesToOneAccount_InToolAndRest()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var services = scope.ServiceProvider;

        await services.GetRequiredService<AccountTools>()
            .CreateAccount("Transfer Probe Ledger C", "checking", "USD", 300);
        await services.GetRequiredService<TransactionTools>()
            .LogOwnTransaction("Transfer Probe Ledger C", 41.99, "transfer-probe ledger snack");

        // The chat tool scopes to the named account (and says whose ledger it is showing)…
        var scoped = await services.GetRequiredService<TransactionTools>()
            .SearchTransactions(accountName: "Transfer Probe Ledger C");
        Assert.Contains("Transactions on 'Transfer Probe Ledger C'", scoped);
        Assert.Contains("transfer-probe ledger snack", scoped);
        Assert.DoesNotContain("transfer-probe hook wire out", scoped); // another account's row

        // …an unknown name is an honest miss, not an unfiltered dump…
        Assert.Contains("No account named",
            await services.GetRequiredService<TransactionTools>().SearchTransactions(accountName: "No Such Account"));

        // …and the REST read the Transactions tab uses filters server-side the same way.
        using var admin = fixture.AdminClient();
        var rows = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/finance/transactions?account={Uri.EscapeDataString("Transfer Probe Ledger C")}");
        Assert.True(rows.GetArrayLength() > 0);
        Assert.All(rows.EnumerateArray(),
            r => Assert.Equal("Transfer Probe Ledger C", r.GetProperty("accountName").GetString()));

        var none = await admin.GetFromJsonAsync<JsonElement>("/api/finance/transactions?account=NoSuchAccount");
        Assert.Equal(0, none.GetArrayLength());
    }
}

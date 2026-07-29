using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Plenipo.Application.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Networthy.Finance;
using Networthy.Finance.Persistence;
using Xunit;

namespace Networthy.IntegrationTests;

/// <summary>
/// Statements imported WITHOUT naming an account, end to end through the real job processor:
/// an OFX whose ACCTID matches an existing account auto-attaches; an anonymous CSV waits in
/// needs-account and the review page's row action creates the detected account on the human's
/// confirm; a typo'd explicit name gets "did you mean" instead of a dead end.
/// </summary>
[Collection("api")]
public sealed class StatementAccountDetectionTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task OfxWithAcctId_AutoMatches_TheAccountWithThatMask()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var services = scope.ServiceProvider;

        await services.GetRequiredService<AccountTools>()
            .CreateAccount("Masked Checking", "checking", "USD", 500);
        var db = services.GetRequiredService<FinanceDbContext>();
        var account = await db.Accounts.FirstAsync(a => a.Name == "Masked Checking");
        account.MaskedAccountNumber = "••••7788";
        await db.SaveChangesAsync();

        const string ofx = """
            <OFX><SIGNONMSGSRSV1><SONRS><FI><ORG>Detect Bank</ORG></FI></SONRS></SIGNONMSGSRSV1>
            <BANKACCTFROM><ACCTID>001122337788</ACCTID></BANKACCTFROM>
            <BANKMSGSRSV1><STMTTRNRS><STMTRS><BANKTRANLIST>
            <STMTTRN>
            <TRNTYPE>DEBIT
            <DTPOSTED>20260710
            <TRNAMT>-12.50
            <NAME>DETECTED COFFEE
            </STMTTRN>
            </BANKTRANLIST></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(ofx));
        var stored = await services.GetRequiredService<IFileStore>()
            .SaveAsync("masked-checking.ofx", "application/x-ofx", stream, source: "test");

        // No account named — detection attaches it to the ••••7788 account on its own, and the
        // tool's bounded wait reports that landing in the same call.
        var outcome = await services.GetRequiredService<StatementImportTools>()
            .ImportStatement(stored.Id.ToString());
        Assert.Contains("1 line(s)", outcome);
        Assert.Contains("'Masked Checking'", outcome);

        await WaitForBatchAsync(stored.Id, expected: "parsed");

        using var check = fixture.Factory.Services.CreateScope();
        var batch = await check.ServiceProvider.GetRequiredService<FinanceDbContext>()
            .ImportBatches.IgnoreQueryFilters().FirstAsync(b => b.SourceFileId == stored.Id);
        Assert.Equal(account.Id, batch.AccountId);
        Assert.Equal("••••7788", batch.DetectedAccountMask);
        Assert.Equal("Detect Bank", batch.DetectedInstitution);
    }

    [Fact]
    public async Task AnonymousCsv_WaitsForAccount_AndRowActionCreatesTheDetectedOne()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var services = scope.ServiceProvider;

        // A CSV carries no institution or mask — detection finds nothing, so the batch must ASK.
        var csv = "Date,Description,Amount\n" +
                  "2026-07-18,MYSTERY MERCHANT,-10.00\n" +
                  "2026-07-19,MYSTERY REFUND,25.00\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var stored = await services.GetRequiredService<IFileStore>()
            .SaveAsync("unknown-account.csv", "text/csv", stream, source: "test");

        await services.GetRequiredService<StatementImportTools>().ImportStatement(stored.Id.ToString());
        await WaitForBatchAsync(stored.Id, expected: "needs-account");

        services.GetRequiredService<FinanceDbContext>().ChangeTracker.Clear();

        // Approving too early is refused with directions, not a crash.
        var early = await services.GetRequiredService<StatementImportTools>().ApproveImportBatch("unknown-account.csv");
        Assert.Contains("assign_import_account", early);

        // Review explains the situation and still shows the lines.
        var review = await services.GetRequiredService<StatementImportTools>().ReviewImportBatch("unknown-account.csv");
        Assert.Contains("no existing account matches", review);
        Assert.Contains("MYSTERY MERCHANT", review);

        // The review page shows the honest status…
        using var admin = fixture.AdminClient();
        var rows = await admin.GetFromJsonAsync<JsonElement>("/api/finance/imports/batches");
        var row = rows.EnumerateArray().Single(r => r.GetProperty("fileName").GetString() == "unknown-account.csv");
        Assert.StartsWith("Needs an account", row.GetProperty("status").GetString());

        // …and its row action creates the detected account (file-name fallback here) + attaches.
        var id = row.GetProperty("id").GetGuid();
        var create = await admin.PostAsync($"/api/finance/imports/{id}/create-account", null);
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        services.GetRequiredService<FinanceDbContext>().ChangeTracker.Clear();
        var posted = await services.GetRequiredService<StatementImportTools>().ApproveImportBatch("unknown-account.csv");
        Assert.Contains("Posted 2 transaction(s)", posted);
        Assert.Contains("unknown-account account", posted); // created from the file name, honestly

        // Running the same action twice is a harmless refusal, not a second account.
        var again = await admin.PostAsync($"/api/finance/imports/{id}/create-account", null);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    }

    [Fact]
    public async Task AssignImportAccount_CreateIfMissing_InheritsDetection()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var services = scope.ServiceProvider;

        // PDF-shaped text goes through the document leg; give the CSV leg nothing to grab so the
        // batch needs an account, but the header carries institution + mask for detection.
        const string ofx = """
            <OFX><SIGNONMSGSRSV1><SONRS><FI><ORG>Fresh Bank</ORG></FI></SONRS></SIGNONMSGSRSV1>
            <BANKACCTFROM><ACCTID>55660099</ACCTID></BANKACCTFROM>
            <BANKMSGSRSV1><STMTTRNRS><STMTRS><BANKTRANLIST>
            <STMTTRN>
            <TRNTYPE>DEBIT
            <DTPOSTED>20260720
            <TRNAMT>-33.00
            <NAME>FRESH GROCER
            </STMTTRN>
            </BANKTRANLIST></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(ofx));
        var stored = await services.GetRequiredService<IFileStore>()
            .SaveAsync("fresh-bank.ofx", "application/x-ofx", stream, source: "test");

        await services.GetRequiredService<StatementImportTools>().ImportStatement(stored.Id.ToString());
        await WaitForBatchAsync(stored.Id, expected: "needs-account"); // no ••••0099 account exists

        services.GetRequiredService<FinanceDbContext>().ChangeTracker.Clear();
        var tools = services.GetRequiredService<StatementImportTools>();

        // Without createIfMissing the tool asks rather than creating.
        var asked = await tools.AssignImportAccount("Fresh daily", fileName: "fresh-bank.ofx");
        Assert.Contains("createIfMissing", asked);

        var assigned = await tools.AssignImportAccount(
            "Fresh daily", createIfMissing: true, accountType: "savings", fileName: "fresh-bank.ofx");
        Assert.Contains("'Fresh daily'", assigned);

        var db = services.GetRequiredService<FinanceDbContext>();
        var created = await db.Accounts.FirstAsync(a => a.Name == "Fresh daily");
        Assert.Equal("savings", created.Type);
        Assert.Equal("Fresh Bank", created.InstitutionName);   // inherited from detection
        Assert.Equal("••••0099", created.MaskedAccountNumber); // the next import auto-matches
    }

    [Fact]
    public async Task ExplicitTypo_GetsDidYouMean_NotALimboImport()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var services = scope.ServiceProvider;

        await services.GetRequiredService<AccountTools>()
            .CreateAccount("Household spending", "checking", "USD", 100);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Date,Description,Amount\n2026-07-01,X,-1.00\n"));
        var stored = await services.GetRequiredService<IFileStore>()
            .SaveAsync("typo-test.csv", "text/csv", stream, source: "test");

        var answer = await services.GetRequiredService<StatementImportTools>()
            .ImportStatement(stored.Id.ToString(), "Houshold spnding");

        Assert.Contains("Did you mean 'Household spending'", answer);
        // Nothing was queued for the typo — the suggestion is the whole outcome.
        var db = services.GetRequiredService<FinanceDbContext>();
        Assert.False(await db.ImportBatches.AnyAsync(b => b.SourceFileId == stored.Id));
    }

    private async Task WaitForBatchAsync(Guid sourceFileId, string expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = fixture.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FinanceDbContext>();
            var batch = await db.ImportBatches.IgnoreQueryFilters()
                .FirstOrDefaultAsync(b => b.SourceFileId == sourceFileId);
            if (batch is not null && batch.Status != "queued")
            {
                Assert.Equal(expected, batch.Status);
                return;
            }

            await Task.Delay(500);
        }

        Assert.Fail($"Import batch for file {sourceFileId} did not leave 'queued' within 30s — is the job processor running?");
    }
}

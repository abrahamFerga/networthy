using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Plenipo.Application.Documents;
using Plenipo.Application.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Networthy.Finance;
using Networthy.Finance.Persistence;
using Xunit;

namespace Networthy.IntegrationTests;

/// <summary>
/// The two ways a household's statement import used to cost them, proven end to end through the
/// real job processor and the real review→approve gate.
/// <para>
/// First: a statement that names its account number met an account that had never recorded one,
/// so it asked "which account is this?" — and asked again the next month, and the month after,
/// because answering resolved the batch but never taught the account. Now the answer sticks and
/// the next statement matches itself.
/// </para>
/// <para>
/// Second: the same month uploaded twice — the bank's PDF, then its CSV export — used to post
/// every transaction a second time and quietly inflate the balance. Now approving the second file
/// posts only what the account does not already hold, and says so before and after.
/// </para>
/// </summary>
[Collection("api")]
public sealed class StatementReimportJourneyTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task AssignedAccount_LearnsTheStatementsNumber_SoTheNextStatementMatchesItself()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var services = scope.ServiceProvider;

        // The household's account as a person actually creates it: a name, and no account number.
        await services.GetRequiredService<AccountTools>()
            .CreateAccount("Everyday spending", "checking", "MXN", 5000);

        // June's statement identifies itself by number. Nothing on file carries it, so the import
        // has to ask — this is the screenshot in the issue.
        var june = await UploadStatementPdfAsync(services, "estado-junio.pdf", "Bancodemo Estado De Cuenta",
            "Cuenta ****5150",   // the renderer used here cannot encode "•"; both spellings are real
            "Periodo del 01 al 30 de junio de 2026",
            "2026-06-03 TIENDA EJEMPLO -92.50",
            "2026-06-15 NOMINA DEPOSITO 24,000.00");

        await services.GetRequiredService<StatementImportTools>().ImportStatement(june.ToString());
        await WaitForBatchAsync(june, expected: "needs-account");
        services.GetRequiredService<FinanceDbContext>().ChangeTracker.Clear();

        var review = await services.GetRequiredService<StatementImportTools>().ReviewImportBatch("estado-junio.pdf");
        Assert.Contains("no existing account matches", review);
        Assert.Contains("••••5150", review);

        // The human answers once, naming the account they already have.
        var assigned = await services.GetRequiredService<StatementImportTools>()
            .AssignImportAccount("Everyday spending", fileName: "estado-junio.pdf");
        Assert.Contains("Everyday spending", assigned);
        Assert.Contains("••••5150", assigned); // and says it learned the number

        // The account itself now carries the number — that is what makes the answer stick.
        using (var check = fixture.Factory.Services.CreateScope())
        {
            var account = await check.ServiceProvider.GetRequiredService<FinanceDbContext>()
                .Accounts.IgnoreQueryFilters().FirstAsync(a => a.Name == "Everyday spending");
            Assert.Equal("••••5150", account.MaskedAccountNumber);
            Assert.Equal("Bancodemo", account.InstitutionName);
        }

        services.GetRequiredService<FinanceDbContext>().ChangeTracker.Clear();
        Assert.Contains("Posted 2 transaction(s)",
            await services.GetRequiredService<StatementImportTools>().ApproveImportBatch("estado-junio.pdf"));

        // July's statement, same account number, uploaded with no account named: it resolves
        // itself. The household is never asked the same question twice.
        var july = await UploadStatementPdfAsync(services, "estado-julio.pdf", "Bancodemo Estado De Cuenta",
            "Cuenta ****5150",   // the renderer used here cannot encode "•"; both spellings are real
            "Periodo del 01 al 31 de julio de 2026",
            "2026-07-04 FARMACIA EJEMPLO -310.00");

        var outcome = await services.GetRequiredService<StatementImportTools>().ImportStatement(july.ToString());
        Assert.Contains("'Everyday spending'", outcome);
        await WaitForBatchAsync(july, expected: "parsed");

        using var after = fixture.Factory.Services.CreateScope();
        var db = after.ServiceProvider.GetRequiredService<FinanceDbContext>();
        var everyday = await db.Accounts.IgnoreQueryFilters().FirstAsync(a => a.Name == "Everyday spending");
        var julyBatch = await db.ImportBatches.IgnoreQueryFilters().FirstAsync(b => b.SourceFileId == july);
        Assert.Equal(everyday.Id, julyBatch.AccountId);
    }

    [Fact]
    public async Task SamePeriodImportedTwice_PostsEachTransactionOnce()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var services = scope.ServiceProvider;

        await services.GetRequiredService<AccountTools>()
            .CreateAccount("Reimport Checking", "checking", "USD", 1000);

        // The bank's PDF for June.
        var pdf = await UploadStatementPdfAsync(services, "june-statement.pdf", "June Account Summary",
            "2026-06-05 TIENDA EJEMPLO -54.10",
            "2026-06-12 ACME PAYROLL DEPOSIT 2,000.00",
            "2026-06-20 STREAMING SERVICE -15.49");

        await services.GetRequiredService<StatementImportTools>()
            .ImportStatement(pdf.ToString(), "Reimport Checking");
        await WaitForBatchAsync(pdf, expected: "parsed");
        services.GetRequiredService<FinanceDbContext>().ChangeTracker.Clear();

        var firstPost = await services.GetRequiredService<StatementImportTools>()
            .ApproveImportBatch("june-statement.pdf");
        Assert.Contains("Posted 3 transaction(s)", firstPost);
        Assert.Contains("2,930.41", firstPost); // 1000 - 54.10 + 2000 - 15.49

        // Then the same month as the bank's CSV export: the same three movements, plus one the
        // PDF's page break had cut off.
        var csv = "Date,Description,Amount\n" +
                  "2026-06-05,TIENDA EJEMPLO,-54.10\n" +
                  "2026-06-12,ACME PAYROLL DEPOSIT,2000.00\n" +
                  "2026-06-20,STREAMING SERVICE,-15.49\n" +
                  "2026-06-28,FARMACIA EJEMPLO,-31.00\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var stored = await services.GetRequiredService<IFileStore>()
            .SaveAsync("june-export.csv", "text/csv", stream, source: "test");

        await services.GetRequiredService<StatementImportTools>()
            .ImportStatement(stored.Id.ToString(), "Reimport Checking");
        await WaitForBatchAsync(stored.Id, expected: "parsed");
        services.GetRequiredService<FinanceDbContext>().ChangeTracker.Clear();

        // The reviewer is told the exact overlap BEFORE approving — not just that periods collide.
        var review = await services.GetRequiredService<StatementImportTools>().ReviewImportBatch("june-export.csv");
        Assert.Contains("3 of these 4 line(s) are already posted", review);
        Assert.Contains("post the remaining 1", review);

        // …and the review page says the same thing where the reviewer is actually looking.
        using (var admin = fixture.AdminClient())
        {
            var rows = await admin.GetFromJsonAsync<JsonElement>("/api/finance/imports/batches");
            var id = rows.EnumerateArray().Single(r => r.GetProperty("fileName").GetString() == "june-export.csv")
                .GetProperty("id").GetGuid();
            var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/finance/imports/{id}/detail");
            var alreadyPosted = detail.GetProperty("sections").EnumerateArray()
                .Single(s => s.GetProperty("heading").GetString() == "Already posted");
            Assert.Contains("3 of these 4 line(s)", alreadyPosted.GetProperty("text").GetString());
        }

        var secondPost = await services.GetRequiredService<StatementImportTools>()
            .ApproveImportBatch("june-export.csv");
        Assert.Contains("Posted 1 transaction(s)", secondPost);
        Assert.Contains("Skipped 3 line(s) already posted", secondPost);
        Assert.Contains("2,899.41", secondPost); // 2,930.41 - 31.00, moved by the new line ALONE

        // The ledger holds four transactions, not seven, and the balance is arithmetic on four.
        using var after = fixture.Factory.Services.CreateScope();
        var db = after.ServiceProvider.GetRequiredService<FinanceDbContext>();
        var account = await db.Accounts.IgnoreQueryFilters().FirstAsync(a => a.Name == "Reimport Checking");
        var posted = await db.Transactions.IgnoreQueryFilters()
            .Where(t => t.AccountId == account.Id).ToListAsync();
        Assert.Equal(4, posted.Count);
        Assert.Single(posted, t => t.Description == "TIENDA EJEMPLO");
        Assert.Equal(1000m + posted.Sum(t => t.BalanceDelta), account.CachedBalance);
    }

    [Fact]
    public async Task ReimportingTheExactSameFile_PostsNothingAndSaysSo()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var services = scope.ServiceProvider;

        await services.GetRequiredService<AccountTools>()
            .CreateAccount("Twice Checking", "checking", "USD", 200);

        const string csv = "Date,Description,Amount\n2026-05-04,CORNER STORE,-12.00\n";
        var first = await SaveCsvAsync(services, "may-first.csv", csv);
        await services.GetRequiredService<StatementImportTools>()
            .ImportStatement(first.ToString(), "Twice Checking");
        await WaitForBatchAsync(first, expected: "parsed");
        services.GetRequiredService<FinanceDbContext>().ChangeTracker.Clear();
        Assert.Contains("Posted 1 transaction(s)",
            await services.GetRequiredService<StatementImportTools>().ApproveImportBatch("may-first.csv"));

        // The same file again, under a different name — a duplicate upload, the commonest mistake.
        var second = await SaveCsvAsync(services, "may-again.csv", csv);
        await services.GetRequiredService<StatementImportTools>()
            .ImportStatement(second.ToString(), "Twice Checking");
        await WaitForBatchAsync(second, expected: "parsed");
        services.GetRequiredService<FinanceDbContext>().ChangeTracker.Clear();

        var posted = await services.GetRequiredService<StatementImportTools>().ApproveImportBatch("may-again.csv");
        Assert.Contains("Nothing new to post", posted);
        Assert.Contains("188.00", posted); // 200 - 12.00, unchanged by the second approval

        using var after = fixture.Factory.Services.CreateScope();
        var db = after.ServiceProvider.GetRequiredService<FinanceDbContext>();
        var account = await db.Accounts.IgnoreQueryFilters().FirstAsync(a => a.Name == "Twice Checking");
        Assert.Equal(1, await db.Transactions.IgnoreQueryFilters().CountAsync(t => t.AccountId == account.Id));
        Assert.Equal(188m, account.CachedBalance);
    }

    private static async Task<Guid> UploadStatementPdfAsync(
        IServiceProvider services, string fileName, string title, params string[] rows)
    {
        // One row per paragraph — how the platform's renderer lays a statement out, and the exact
        // shape the document reader hands back.
        var pdf = services.GetRequiredService<IPdfRenderer>().Render(title, string.Join("\n\n", rows));
        using var stream = new MemoryStream(pdf);
        var stored = await services.GetRequiredService<IFileStore>()
            .SaveAsync(fileName, "application/pdf", stream, source: "test");
        return stored.Id;
    }

    private static async Task<Guid> SaveCsvAsync(IServiceProvider services, string fileName, string csv)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var stored = await services.GetRequiredService<IFileStore>()
            .SaveAsync(fileName, "text/csv", stream, source: "test");
        return stored.Id;
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

        Assert.Fail($"Batch for file {sourceFileId} never left 'queued'.");
    }
}

using System.Text;
using Plenipo.Application.Documents;
using Plenipo.Application.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Networthy.Finance;
using Networthy.Finance.Persistence;
using Xunit;

namespace Networthy.IntegrationTests;

/// <summary>
/// The statement pipeline end to end, through the REAL moving parts: the platform file store,
/// the background job processor (extraction runs as a queued job, not inline), and the two-step
/// review→approve gate. The PDF case generates a statement with the platform's own renderer and
/// reads it back through <see cref="IDocumentReader"/> — the exact path a customer's digital PDF
/// takes (scanned PDFs add the OCR capability, e.g. Azure Document Intelligence, on the same seam).
/// </summary>
[Collection("api")]
public sealed class StatementImportTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task CsvStatement_Import_Parse_Review_Approve_PostsTransactions()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var services = scope.ServiceProvider;

        await services.GetRequiredService<AccountTools>().CreateAccount("CSV Checking", "checking", "USD", 1000);

        var csv = "Date,Description,Amount\n" +
                  "2026-07-01,WHOLE FOODS MARKET,-82.45\n" +
                  "2026-07-02,ACME PAYROLL,2500.00\n" +
                  "2026-07-03,SHELL GASOLINE,-41.20\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var stored = await services.GetRequiredService<IFileStore>()
            .SaveAsync("bank-export.csv", "text/csv", stream, source: "test");

        var queued = await services.GetRequiredService<StatementImportTools>()
            .ImportStatement(stored.Id.ToString(), "CSV Checking");
        Assert.Contains("queued for extraction", queued);

        // The job processor (real hosted service, 1s poll) picks the batch up. The job mutated the
        // row in its own scope — drop this scope's tracked copy so the tools re-read fresh state.
        await WaitForBatchAsync(stored.Id, expected: "parsed");
        services.GetRequiredService<FinanceDbContext>().ChangeTracker.Clear();

        var review = await services.GetRequiredService<StatementImportTools>().ReviewImportBatch("bank-export.csv");
        Assert.Contains("3 line(s)", review);
        Assert.Contains("WHOLE FOODS MARKET", review);
        Assert.Contains("[suggest: Groceries]", review);   // merchant keywords bound to the seeded taxonomy
        Assert.Contains("+2,500.00", review);              // positive CSV amount → income

        var posted = await services.GetRequiredService<StatementImportTools>().ApproveImportBatch("bank-export.csv");
        Assert.Contains("Posted 3 transaction(s)", posted);
        Assert.Contains("3,376.35", posted);               // 1000 - 82.45 + 2500 - 41.20

        var search = await services.GetRequiredService<TransactionTools>().SearchTransactions(text: "shell");
        Assert.Contains("SHELL GASOLINE", search);
    }

    [Fact]
    public async Task PdfStatement_ExtractsThroughPlatformDocumentReader_AndPosts()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var services = scope.ServiceProvider;

        await services.GetRequiredService<AccountTools>().CreateAccount("PDF Checking", "checking", "USD", 500);

        // A digital PDF statement, produced by the platform's own renderer — one row per paragraph.
        var body = string.Join("\n\n",
            "2026-07-01 TRADER JOES -54.10",
            "2026-07-02 ACME PAYROLL DEPOSIT 2,000.00",
            "2026-07-04 NETFLIX.COM -15.49");
        var pdfBytes = services.GetRequiredService<IPdfRenderer>().Render("Checking statement July 2026", body);

        using var stream = new MemoryStream(pdfBytes);
        var stored = await services.GetRequiredService<IFileStore>()
            .SaveAsync("bank-digital.pdf", "application/pdf", stream, source: "test");

        await services.GetRequiredService<StatementImportTools>().ImportStatement(stored.Id.ToString(), "PDF Checking");
        await WaitForBatchAsync(stored.Id, expected: "parsed");
        services.GetRequiredService<FinanceDbContext>().ChangeTracker.Clear();

        var review = await services.GetRequiredService<StatementImportTools>().ReviewImportBatch("bank-digital.pdf");
        Assert.Contains("3 line(s)", review);
        Assert.Contains("TRADER JOES", review);
        Assert.Contains("+2,000.00", review);              // "DEPOSIT" income hint on the unsigned amount
        Assert.Contains("[suggest: Subscriptions]", review); // NETFLIX → Subscriptions via merchant keywords

        var posted = await services.GetRequiredService<StatementImportTools>().ApproveImportBatch("bank-digital.pdf");
        Assert.Contains("Posted 3 transaction(s)", posted);
        Assert.Contains("2,430.41", posted);               // 500 - 54.10 + 2000 - 15.49
    }

    [Fact]
    public async Task UnreadableFile_FailsHonestly_WithTheDocumentReaderStory()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var services = scope.ServiceProvider;

        await services.GetRequiredService<AccountTools>().CreateAccount("Junk Target", "checking", "USD");

        using var stream = new MemoryStream([0x00, 0x01, 0x02, 0x03]);
        var stored = await services.GetRequiredService<IFileStore>()
            .SaveAsync("opaque-junk.bin", "application/octet-stream", stream, source: "test");

        await services.GetRequiredService<StatementImportTools>().ImportStatement(stored.Id.ToString(), "Junk Target");
        await WaitForBatchAsync(stored.Id, expected: "failed");
        services.GetRequiredService<FinanceDbContext>().ChangeTracker.Clear();

        var review = await services.GetRequiredService<StatementImportTools>().ReviewImportBatch("opaque-junk.bin");
        Assert.Contains("failed extraction", review);
        Assert.Contains("CSV and OFX/QFX", review);
    }

    private async Task WaitForBatchAsync(Guid sourceFileId, string expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = Factory().CreateScope();
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

    private IServiceProvider Factory() => fixture.Factory.Services;
}

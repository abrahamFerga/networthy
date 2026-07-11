using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cortex.Application.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Networthy.Finance;
using Networthy.Finance.Persistence;
using Xunit;

namespace Networthy.IntegrationTests;

/// <summary>
/// Multi-batch statement review, end to end: two statements imported through the real pipeline,
/// BOTH visible in the batch listing, the OLDER one reviewable and approvable by id (the thing
/// the latest-only surface could never do), the detail document the Review tab drills into, and
/// the RBAC gate on the per-batch approve route.
/// </summary>
[Collection("api")]
public sealed class MultiBatchReviewTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task TwoBatches_BothListed_OlderReviewableAndApprovableById_NewerStaysPending()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var services = scope.ServiceProvider;

        await services.GetRequiredService<AccountTools>().CreateAccount("Multi Batch Checking", "checking", "USD", 100);

        // Two statements, imported sequentially so their CreatedAt order is unambiguous. The
        // whole collection shares one tenant, so the file names are deliberately distinctive.
        var olderId = await ImportAsync(services, "multibatch-older.csv",
            "Date,Description,Amount\n" +
            "2026-07-01,MULTIBATCH OLDER COFFEE,-4.50\n" +
            "2026-07-02,MULTIBATCH OLDER PAYROLL,100.00\n");
        var newerId = await ImportAsync(services, "multibatch-newer.csv",
            "Date,Description,Amount\n" +
            "2026-07-03,MULTIBATCH NEWER GROCER,-20.00\n");
        services.GetRequiredService<FinanceDbContext>().ChangeTracker.Clear();

        // The chat side can enumerate before picking: both pending batches, by name.
        var listing = await services.GetRequiredService<StatementImportTools>().ListImportBatches();
        Assert.Contains("multibatch-older.csv", listing);
        Assert.Contains("multibatch-newer.csv", listing);
        Assert.Contains("2 line(s) awaiting review", listing);

        using var client = fixture.AdminClient();

        // (a) The batch listing the Review tab now renders shows BOTH parsed batches.
        var batches = await client.GetFromJsonAsync<JsonElement>("/api/finance/imports/batches");
        var older = batches.EnumerateArray().Single(b => b.GetProperty("fileName").GetString() == "multibatch-older.csv");
        var newer = batches.EnumerateArray().Single(b => b.GetProperty("fileName").GetString() == "multibatch-newer.csv");
        Assert.Equal(2, older.GetProperty("lineCount").GetInt32());
        Assert.Equal(1, newer.GetProperty("lineCount").GetInt32());
        var olderBatchId = older.GetProperty("id").GetString()!;
        var newerBatchId = newer.GetProperty("id").GetString()!;

        // The latest route still answers with the NEWEST parsed batch…
        var latest = await client.GetFromJsonAsync<JsonElement>("/api/finance/imports/latest/lines");
        Assert.Equal("multibatch-newer.csv", latest[0].GetProperty("batch").GetString());

        // (b) …while the {batchId} route reaches the OLDER one — previously stranded.
        var olderLines = await client.GetFromJsonAsync<JsonElement>($"/api/finance/imports/{olderBatchId}/lines");
        Assert.Equal(2, olderLines.GetArrayLength());
        Assert.Equal("MULTIBATCH OLDER COFFEE", olderLines[0].GetProperty("description").GetString());

        // Per-batch line correction works on the non-latest batch too.
        var fix = await client.PostAsJsonAsync($"/api/finance/imports/{olderBatchId}/lines", new { index = 0, category = "Dining" });
        Assert.Equal(HttpStatusCode.OK, fix.StatusCode);
        var corrected = await client.GetFromJsonAsync<JsonElement>($"/api/finance/imports/{olderBatchId}/lines");
        Assert.Equal("Dining", corrected[0].GetProperty("category").GetString());

        // (c) Approving the OLDER batch by id posts its lines; the newer batch stays pending.
        var approve = await client.PostAsync($"/api/finance/imports/{olderBatchId}/approve", null);
        var message = (await approve.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString();
        Assert.Contains("Posted 2 transaction(s)", message);

        var remaining = await client.GetFromJsonAsync<JsonElement>("/api/finance/imports/batches");
        var names = remaining.EnumerateArray().Select(b => b.GetProperty("fileName").GetString()).ToList();
        Assert.DoesNotContain("multibatch-older.csv", names);
        Assert.Contains("multibatch-newer.csv", names);

        var search = await services.GetRequiredService<TransactionTools>().SearchTransactions(text: "multibatch older");
        Assert.Contains("[Dining]", search);
        Assert.DoesNotContain("NEWER", search); // nothing from the pending batch posted

        // (d) The Review tab's drill-down: a detail document of the batch's lines.
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/finance/imports/{newerBatchId}/detail");
        Assert.Equal("multibatch-newer.csv", detail.GetProperty("title").GetString());
        Assert.Contains("parsed", detail.GetProperty("subtitle").GetString());
        var section = detail.GetProperty("sections")[0];
        Assert.Contains("Extracted lines", section.GetProperty("heading").GetString());
        Assert.Equal("MULTIBATCH NEWER GROCER", section.GetProperty("rows")[0].GetProperty("description").GetString());

        // The per-batch approve route is gated like the rest of the review surface.
        using var member = fixture.Factory.CreateClient();
        member.DefaultRequestHeaders.Add("X-Dev-Subject", "it-member-multibatch");
        member.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        member.DefaultRequestHeaders.Add("X-Dev-Roles", "household-member");
        var denied = await member.PostAsync($"/api/finance/imports/{newerBatchId}/approve", null);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        // Leave the shared tenant clean: approve the newer batch by id as well.
        var finish = await client.PostAsync($"/api/finance/imports/{newerBatchId}/approve", null);
        Assert.Equal(HttpStatusCode.OK, finish.StatusCode);
    }

    /// <summary>Stores a CSV, imports it through the real tool, and waits for the job processor
    /// to parse it — the exact path a customer's upload takes.</summary>
    private async Task<Guid> ImportAsync(IServiceProvider services, string fileName, string csv)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var stored = await services.GetRequiredService<IFileStore>()
            .SaveAsync(fileName, "text/csv", stream, source: "test");
        await services.GetRequiredService<StatementImportTools>()
            .ImportStatement(stored.Id.ToString(), "Multi Batch Checking");
        await WaitForParsedAsync(stored.Id);
        return stored.Id;
    }

    private async Task WaitForParsedAsync(Guid sourceFileId)
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
                Assert.Equal("parsed", batch.Status);
                return;
            }

            await Task.Delay(500);
        }

        Assert.Fail($"Import batch for {sourceFileId} did not parse within 30s.");
    }
}

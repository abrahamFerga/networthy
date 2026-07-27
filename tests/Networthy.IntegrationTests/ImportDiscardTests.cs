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
/// The way OUT of the import pipeline: a duplicate upload (or abandoned import) is discarded —
/// by the chat tool or the Review tab's row action — without touching anything posted. Approved
/// batches refuse on every route: their lines are in the ledger and their period arms the
/// duplicate-period warning.
/// </summary>
[Collection("api")]
public sealed class ImportDiscardTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task UnpostedBatch_DiscardsByToolAndByRowAction_ApprovedRefuses()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var services = scope.ServiceProvider;
        var imports = services.GetRequiredService<StatementImportTools>();

        await services.GetRequiredService<AccountTools>().CreateAccount("Discard Probe Checking", "checking", "USD", 0);

        // A duplicate upload the household decided not to post: the chat tool removes it.
        await ImportAsync(services, "discard-tool.csv", "2026-07-04,DISCARD TOOL ROW,-1.00\n");
        var byTool = await imports.DiscardImportBatch("discard-tool.csv");
        Assert.Contains("Discarded 'discard-tool.csv'", byTool);
        Assert.Contains("nothing had posted", byTool, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("discard-tool.csv", await imports.ListImportBatches());

        // The Review tab's row action does the same by id…
        var rowActionFileId = await ImportAsync(services, "discard-row.csv", "2026-07-05,DISCARD ROW ACTION,-2.00\n");
        using var client = fixture.AdminClient();
        var rowBatchId = await BatchIdAsync(rowActionFileId);
        var discarded = await client.PostAsync($"/api/finance/imports/{rowBatchId}/discard", null);
        Assert.Equal(HttpStatusCode.OK, discarded.StatusCode);
        var rows = await client.GetFromJsonAsync<JsonElement>("/api/finance/imports/batches");
        Assert.DoesNotContain(rows.EnumerateArray(), r => r.GetProperty("fileName").GetString() == "discard-row.csv");

        // …but neither route can discard an APPROVED batch.
        var approvedFileId = await ImportAsync(services, "discard-approved.csv", "2026-07-06,DISCARD APPROVED ROW,-3.00\n");
        var approvedBatchId = await BatchIdAsync(approvedFileId);
        (await client.PostAsync($"/api/finance/imports/{approvedBatchId}/approve", null)).EnsureSuccessStatusCode();
        // The approve ran in the ENDPOINT's scope — drop this scope's stale tracked copy so the
        // tool sees the approved status (same convention as MultiBatchReviewTests).
        services.GetRequiredService<FinanceDbContext>().ChangeTracker.Clear();
        var refusedByTool = await imports.DiscardImportBatch("discard-approved.csv");
        Assert.Contains("cannot be discarded", refusedByTool);
        var refusedByEndpoint = await client.PostAsync($"/api/finance/imports/{approvedBatchId}/discard", null);
        Assert.Equal(HttpStatusCode.BadRequest, refusedByEndpoint.StatusCode);

        // The discard route is gated like the rest of the manage surface.
        using var member = fixture.Factory.CreateClient();
        member.DefaultRequestHeaders.Add("X-Dev-Subject", "it-member-discard");
        member.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        member.DefaultRequestHeaders.Add("X-Dev-Roles", "household-member");
        var denied = await member.PostAsync($"/api/finance/imports/{approvedBatchId}/discard", null);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    private async Task<Guid> ImportAsync(IServiceProvider services, string fileName, string rows)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Date,Description,Amount\n" + rows));
        var stored = await services.GetRequiredService<IFileStore>()
            .SaveAsync(fileName, "text/csv", stream, source: "test");
        await services.GetRequiredService<StatementImportTools>()
            .ImportStatement(stored.Id.ToString(), "Discard Probe Checking");
        await WaitForParsedAsync(stored.Id);
        return stored.Id;
    }

    private async Task<Guid> BatchIdAsync(Guid sourceFileId)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinanceDbContext>();
        return (await db.ImportBatches.IgnoreQueryFilters().FirstAsync(b => b.SourceFileId == sourceFileId)).Id;
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

        Assert.Fail($"Batch for {sourceFileId} never parsed.");
    }
}

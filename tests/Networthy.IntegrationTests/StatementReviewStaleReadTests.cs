using System.Text;
using Plenipo.Application.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Networthy.Finance;
using Networthy.Finance.Persistence;
using Xunit;

namespace Networthy.IntegrationTests;

/// <summary>
/// Found via live E2E verification with a real, capable model (not Mock): within ONE chat turn,
/// <c>import_statement</c> then <c>review_import_batch</c> share a single <c>FinanceDbContext</c> —
/// <c>FinanceToolSource</c> resolves one per turn. <c>import_statement</c>'s own <c>Add</c> leaves the
/// batch tracked as "queued"; the background parse job flips it to "parsed" moments later, in its OWN
/// scope, on the SAME database row. Without an explicit reload, EF's identity map hands the SECOND
/// call back the FIRST call's stale tracked snapshot instead of materializing the row's current
/// state — so the assistant told a household extraction was "still being extracted" when it had
/// already finished.
///
/// <see cref="MultiBatchReviewTests"/> independently corroborates this: it calls
/// <c>FinanceDbContext.ChangeTracker.Clear()</c> mid-test, with no explanation, immediately before its
/// OWN next read of the same batches — a manual workaround for exactly this staleness, written before
/// this fix existed. This test proves the workaround is no longer necessary and pins the underlying
/// bug so it cannot come back silently.
/// </summary>
[Collection("api")]
public sealed class StatementReviewStaleReadTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task ReviewImportBatch_SeesTheCurrentStatus_EvenWhenTheSameTurnAlreadyTrackedTheOlderRow()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var services = scope.ServiceProvider;

        await services.GetRequiredService<AccountTools>()
            .CreateAccount("Stale Read Checking", "checking", "USD", 0);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            "Date,Description,Amount\n2026-07-15,STALE READ TEST LINE,-12.34\n"));
        var stored = await services.GetRequiredService<IFileStore>()
            .SaveAsync("stale-read-test.csv", "text/csv", stream, source: "test");

        // Same StatementImportTools instance for both calls, so both go through the SAME
        // FinanceDbContext — exactly what one chat turn shares. Queue-only: no wait, so the batch
        // is Added and tracked as "queued" with nothing else touching this context yet.
        var tools = services.GetRequiredService<StatementImportTools>();
        await tools.QueueStatementImport(stored.Id.ToString(), "Stale Read Checking");

        // The REAL background job, in its OWN scope: parses the file and flips Status for real —
        // no manually poking a field, this is the actual pipeline on a completely separate
        // FinanceDbContext instance, the same way it runs in production.
        await WaitForParsedAsync(stored.Id);

        // Deliberately NOT clearing the change tracker here — that is the manual workaround
        // MultiBatchReviewTests uses; this call proves the fix makes it unnecessary. Same `tools`,
        // same context that has held the "queued" batch tracked since the call above.
        var review = await tools.ReviewImportBatch("stale-read-test.csv");

        Assert.DoesNotContain("still being extracted", review, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("STALE READ TEST LINE", review, StringComparison.Ordinal);
    }

    /// <summary>Polls via a FRESH scope — never the scope under test — for the real job to finish.</summary>
    private async Task WaitForParsedAsync(Guid sourceFileId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var pollScope = fixture.Factory.Services.CreateScope();
            var db = pollScope.ServiceProvider.GetRequiredService<FinanceDbContext>();
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

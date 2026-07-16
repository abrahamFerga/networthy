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
/// The two new surfaces, end to end: goals (chat tools + the tab endpoint) and the statement
/// review tab (lines → correct → drop → approve, over the real import pipeline). Plus the
/// runtime branding and trend endpoints the shell now consumes.
/// </summary>
[Collection("api")]
public sealed class GoalsAndReviewTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task Goals_SetContributeList_ComputeHonestProgress()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var goals = scope.ServiceProvider.GetRequiredService<GoalTools>();

        Assert.Contains("Goal 'Hawaii trip': 5,000.00 USD", await goals.SetGoal("Hawaii trip", 5000));
        Assert.Contains("1,500.00 / 5,000.00", await goals.ContributeToGoal("Hawaii trip", 1500));
        var listing = await goals.ListGoals();
        Assert.Contains("Hawaii trip", listing);
        Assert.Contains("30", Compact(listing)); // 1,500 of 5,000 — percent formatting is culture-shaped

        // The tab endpoint mirrors the same math.
        using var client = fixture.AdminClient();
        var rows = await client.GetFromJsonAsync<JsonElement>("/api/finance/goals");
        var hawaii = rows.EnumerateArray().Single(g => g.GetProperty("name").GetString() == "Hawaii trip");
        Assert.Equal("30%", Compact(hawaii.GetProperty("progress").GetString()!));
    }

    [Fact]
    public async Task Goal_LinkedToAnAccount_ReadsItsBalance_AndRefusesManualContributions()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var services = scope.ServiceProvider;

        await services.GetRequiredService<AccountTools>().CreateAccount("Vacation Savings", "savings", "USD", 800);
        var goals = services.GetRequiredService<GoalTools>();

        Assert.Contains("tracked by 'Vacation Savings'", await goals.SetGoal("Van fund", 2000, accountName: "Vacation Savings"));
        Assert.Contains("800.00 / 2,000.00", await goals.ListGoals());
        Assert.Contains("balance IS the progress", await goals.ContributeToGoal("Van fund", 100));
    }

    [Fact]
    public async Task ReviewTab_Correct_Drop_Approve_PostsTheEditedBatch()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var services = scope.ServiceProvider;

        await services.GetRequiredService<AccountTools>().CreateAccount("Review Checking", "checking", "USD", 100);

        var csv = "Date,Description,Amount\n" +
                  "2026-07-01,MYSTERY MERCHANT,-10.00\n" +
                  "2026-07-02,BOGUS DUPLICATE ROW,-99.00\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var stored = await services.GetRequiredService<IFileStore>()
            .SaveAsync("review-me.csv", "text/csv", stream, source: "test");
        await services.GetRequiredService<StatementImportTools>().ImportStatement(stored.Id.ToString(), "Review Checking");
        await WaitForParsedAsync(stored.Id);

        using var client = fixture.AdminClient();

        // 1. The tab lists the extracted lines.
        var lines = await client.GetFromJsonAsync<JsonElement>("/api/finance/imports/latest/lines");
        Assert.Equal(2, lines.GetArrayLength());
        Assert.Equal("MYSTERY MERCHANT", lines[0].GetProperty("description").GetString());

        // 2. Correct line 0's category; 3. drop the bogus line 1.
        var fix = await client.PostAsJsonAsync("/api/finance/imports/latest/lines", new { index = 0, category = "Dining" });
        Assert.Equal(HttpStatusCode.OK, fix.StatusCode);
        var drop = await client.DeleteAsync("/api/finance/imports/latest/lines/1");
        Assert.Equal(HttpStatusCode.NoContent, drop.StatusCode);

        var edited = await client.GetFromJsonAsync<JsonElement>("/api/finance/imports/latest/lines");
        Assert.Equal(1, edited.GetArrayLength());
        Assert.Equal("Dining", edited[0].GetProperty("category").GetString());

        // 4. The approve action posts what the reviewer sees — one transaction, categorized Dining.
        var approve = await client.PostAsync("/api/finance/imports/latest/approve", null);
        var message = (await approve.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString();
        Assert.Contains("Posted 1 transaction(s)", message);

        var search = await services.GetRequiredService<TransactionTools>().SearchTransactions(text: "mystery");
        Assert.Contains("[Dining]", search);
        Assert.DoesNotContain("BOGUS", search);
    }

    [Fact]
    public async Task ReviewEndpoints_AreDeniedTo_HouseholdMembers()
    {
        using var member = fixture.Factory.CreateClient();
        member.DefaultRequestHeaders.Add("X-Dev-Subject", "it-member-review");
        member.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        member.DefaultRequestHeaders.Add("X-Dev-Roles", "household-member");

        Assert.Equal(HttpStatusCode.Forbidden,
            (await member.GetAsync("/api/finance/imports/batches")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await member.GetAsync("/api/finance/imports/latest/lines")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await member.PostAsync("/api/finance/imports/latest/approve", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await member.GetAsync("/api/finance/net-worth/history")).StatusCode);
    }

    [Fact]
    public async Task TrendEndpoint_ServesSnapshots_ForTheChartTab()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        // Guarantee at least one account exists so the sweep has something to snapshot,
        // regardless of which test in the collection runs first.
        await scope.ServiceProvider.GetRequiredService<AccountTools>()
            .CreateAccount("Trend Anchor", "checking", "USD", 42);
        await NetWorthSnapshotService.SweepOnceAsync(scope.ServiceProvider);

        using var client = fixture.AdminClient();
        var rows = await client.GetFromJsonAsync<JsonElement>("/api/finance/net-worth/history");
        Assert.True(rows.GetArrayLength() >= 1);
        var first = rows[0];
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", first.GetProperty("takenOn").GetString());
        Assert.Equal(JsonValueKind.Number, first.GetProperty("netWorth").ValueKind);
    }

    [Fact]
    public async Task RuntimeBranding_AnswersNetworthy()
    {
        using var client = fixture.Factory.CreateClient(); // anonymous on purpose
        var branding = await client.GetFromJsonAsync<JsonElement>("/api/platform/branding");
        Assert.Equal("Networthy", branding.GetProperty("name").GetString());
    }

    /// <summary>Strips every whitespace flavor — percent formatting varies by culture ("30 %").</summary>
    private static string Compact(string s) => string.Concat(s.Where(c => !char.IsWhiteSpace(c)));

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

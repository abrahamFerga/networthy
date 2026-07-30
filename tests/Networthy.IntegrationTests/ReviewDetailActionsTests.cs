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
/// The Review tab's detail document carries the batch's OWN actions (the fix for "the only
/// visible button targeted a hidden 'latest' and silently did nothing"): actions are composed
/// per batch state, a needs-account batch explains itself and offers the assign picker, the
/// assign endpoint attaches an existing account, and refusals are error statuses — never a
/// 200 the UI can mistake for success.
/// </summary>
[Collection("api")]
public sealed class ReviewDetailActionsTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task ReviewListing_OnlyOffersTheStateSafeDiscardRowAction()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        using var admin = fixture.AdminClient();

        var modules = await admin.GetFromJsonAsync<JsonElement>("/api/platform/modules");
        var finance = modules.EnumerateArray().Single(module => module.GetProperty("id").GetString() == "finance");
        var review = finance.GetProperty("tabs").EnumerateArray()
            .Single(tab => tab.GetProperty("id").GetString() == "review");

        var action = Assert.Single(review.GetProperty("rowActions").EnumerateArray());
        Assert.Equal("discard", action.GetProperty("id").GetString());
        Assert.Equal("/api/finance/imports/{id}/discard", action.GetProperty("endpointTemplate").GetString());
    }

    [Fact]
    public async Task ReconciliationWarning_IsVisibleBeforeTheReviewerCanApprove()
    {
        var (scope, tenantId, userId) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<FinanceDbContext>();
        await services.GetRequiredService<AccountTools>()
            .CreateAccount("Warning Review Checking", "checking", "USD", 0);
        var account = await db.Accounts.SingleAsync(a => a.Name == "Warning Review Checking");

        var batch = new StatementImportBatch
        {
            TenantId = tenantId,
            SourceFileId = Guid.NewGuid(),
            FileName = "reconciliation-warning.csv",
            Status = "parsed",
            AccountId = account.Id,
            CreatedByUserId = userId,
            ExtractedLinesJson = "[{\"date\":\"2026-07-29\",\"description\":\"WARNING TEST\",\"amount\":12.34,\"direction\":\"expense\",\"suggestedCategory\":null}]",
            ReviewWarning = "The model found transaction lines, but their totals did not reconcile with the statement summary. Review every line and the statement totals before approval.",
        };
        db.ImportBatches.Add(batch);
        await db.SaveChangesAsync();

        using var admin = fixture.AdminClient();
        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/finance/imports/{batch.Id}/detail");
        // The heading SHOUTS while TODO(plenipo#65) stands: the shell renders every detail section
        // in the same grey, so severity has nowhere to live but this string. When the platform
        // ships section tone, this becomes a plain "Review warning" plus tone = "warning" — and
        // PlatformShimGuardTests is what goes red to say so.
        var warningSection = detail.GetProperty("sections")[0];
        Assert.Contains("REVIEW WARNING", warningSection.GetProperty("heading").GetString());
        Assert.Contains("did not reconcile", warningSection.GetProperty("text").GetString());

        var rows = await admin.GetFromJsonAsync<JsonElement>("/api/finance/imports/batches");
        var row = rows.EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == batch.Id);
        Assert.Contains("reconciliation warning", row.GetProperty("status").GetString());
        // The destination account is on the row itself — approving posts into it.
        Assert.Equal(account.Name, row.GetProperty("accountName").GetString());

        var discard = await admin.PostAsync($"/api/finance/imports/{batch.Id}/discard", null);
        Assert.Equal(HttpStatusCode.OK, discard.StatusCode);
    }

    [Fact]
    public async Task NeedsAccountBatch_ExplainsItself_AssignsViaPicker_ThenApproves()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var services = scope.ServiceProvider;

        // An anonymous CSV detects nothing → the batch waits in needs-account.
        var csv = "Date,Description,Amount\n" +
                  "2026-07-21,DETAILACT MERCHANT,-15.00\n" +
                  "2026-07-22,DETAILACT REFUND,40.00\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var stored = await services.GetRequiredService<IFileStore>()
            .SaveAsync("detail-actions.csv", "text/csv", stream, source: "test");
        await services.GetRequiredService<StatementImportTools>().ImportStatement(stored.Id.ToString());
        await WaitForBatchAsync(stored.Id, expected: "needs-account");

        using var admin = fixture.AdminClient();
        var rows = await admin.GetFromJsonAsync<JsonElement>("/api/finance/imports/batches");
        var id = rows.EnumerateArray().Single(r => r.GetProperty("fileName").GetString() == "detail-actions.csv")
            .GetProperty("id").GetGuid();

        // Approving a needs-account batch REFUSES with an error status, not a quiet 200.
        var refused = await admin.PostAsync($"/api/finance/imports/{id}/approve", null);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("has no account yet", await refused.Content.ReadAsStringAsync());

        // The detail document leads with WHY it's blocked…
        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/finance/imports/{id}/detail");
        Assert.Equal("Needs an account", detail.GetProperty("sections")[0].GetProperty("heading").GetString());

        // …and offers exactly the applicable ways out: assign (with the account picker) and
        // discard. No approve (not approvable), no create-detected (a bare CSV detected nothing).
        var actions = detail.GetProperty("actions").EnumerateArray().ToList();
        var ids = actions.Select(a => a.GetProperty("id").GetString()).ToList();
        Assert.Contains("assign-account", ids);
        Assert.Contains("discard", ids);
        Assert.DoesNotContain("approve", ids);
        Assert.DoesNotContain("create-detected-account", ids);
        var assign = actions.Single(a => a.GetProperty("id").GetString() == "assign-account");
        Assert.Equal("accountName", assign.GetProperty("field").GetProperty("field").GetString());
        Assert.Equal("/api/finance/accounts", assign.GetProperty("field").GetProperty("optionsEndpoint").GetString());

        // Assigning an EXISTING account through the picker's endpoint flips the batch to parsed.
        await services.GetRequiredService<AccountTools>()
            .CreateAccount("Detail Actions Checking", "checking", "USD", 100);
        var assigned = await admin.PostAsJsonAsync(
            $"/api/finance/imports/{id}/assign-account", new { accountName = "Detail Actions Checking" });
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        Assert.Contains("Detail Actions Checking", await assigned.Content.ReadAsStringAsync());

        // The refreshed document now offers approve (and no assign — the question is answered)…
        var parsedDetail = await admin.GetFromJsonAsync<JsonElement>($"/api/finance/imports/{id}/detail");
        var parsedIds = parsedDetail.GetProperty("actions").EnumerateArray()
            .Select(a => a.GetProperty("id").GetString()).ToList();
        Assert.Contains("approve", parsedIds);
        Assert.DoesNotContain("assign-account", parsedIds);

        // …and approving posts the lines for real.
        var approve = await admin.PostAsync($"/api/finance/imports/{id}/approve", null);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        var message = (await approve.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString();
        Assert.Contains("Posted 2 transaction(s)", message);

        // A second approve is a refusal with the honest reason, again as an error status.
        var again = await admin.PostAsync($"/api/finance/imports/{id}/approve", null);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Contains("only a parsed batch", await again.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AssignEndpoint_RefusesNonWaitingBatches_AndIsDeniedToMembers()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var services = scope.ServiceProvider;

        await services.GetRequiredService<AccountTools>()
            .CreateAccount("Assign Refusal Checking", "checking", "USD", 50);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            "Date,Description,Amount\n2026-07-23,ASSIGNREFUSAL COFFEE,-3.00\n"));
        var stored = await services.GetRequiredService<IFileStore>()
            .SaveAsync("assign-refusal.csv", "text/csv", stream, source: "test");
        await services.GetRequiredService<StatementImportTools>()
            .ImportStatement(stored.Id.ToString(), "Assign Refusal Checking");
        await WaitForBatchAsync(stored.Id, expected: "parsed");

        using var admin = fixture.AdminClient();
        var rows = await admin.GetFromJsonAsync<JsonElement>("/api/finance/imports/batches");
        var id = rows.EnumerateArray().Single(r => r.GetProperty("fileName").GetString() == "assign-refusal.csv")
            .GetProperty("id").GetGuid();

        // Already has an account — assigning refuses honestly.
        var refuse = await admin.PostAsJsonAsync(
            $"/api/finance/imports/{id}/assign-account", new { accountName = "Assign Refusal Checking" });
        Assert.Equal(HttpStatusCode.BadRequest, refuse.StatusCode);
        Assert.Contains("not waiting for an account", await refuse.Content.ReadAsStringAsync());

        // A parsed batch's detail actions: approve + discard, nothing account-related.
        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/finance/imports/{id}/detail");
        var ids = detail.GetProperty("actions").EnumerateArray()
            .Select(a => a.GetProperty("id").GetString()).ToList();
        Assert.Equal(["approve", "discard"], ids);

        // Household members hold neither ManageFinance nor ReviewImports: assign is forbidden.
        using var member = fixture.Factory.CreateClient();
        member.DefaultRequestHeaders.Add("X-Dev-Subject", "it-member-detail-actions");
        member.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        member.DefaultRequestHeaders.Add("X-Dev-Roles", "household-member");
        var denied = await member.PostAsJsonAsync(
            $"/api/finance/imports/{id}/assign-account", new { accountName = "Assign Refusal Checking" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        // Leave the shared tenant clean.
        var finish = await admin.PostAsync($"/api/finance/imports/{id}/approve", null);
        Assert.Equal(HttpStatusCode.OK, finish.StatusCode);
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

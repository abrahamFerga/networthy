using System.Net.Http.Json;
using System.Text.Json;
using Cortex.Application.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Networthy.Finance;
using Networthy.Finance.Persistence;
using Xunit;

namespace Networthy.IntegrationTests;

/// <summary>
/// The notification inbox feature (#48) end to end: the manifest's categories reach the platform
/// mute switchboard, and one real run of the daily-digest handler — executed exactly the way the
/// platform scheduler runs it, through the registered IJobHandler with a tenant-scoped context —
/// lands batched, linked notifications in the member's platform inbox.
/// </summary>
[Collection("api")]
public sealed class NotificationDigestTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task FinanceCategories_ReachTheMuteSwitchboard()
    {
        using var client = fixture.AdminClient();
        var preferences = await client.GetFromJsonAsync<JsonElement>("/api/notifications/preferences");

        var ids = preferences.EnumerateArray().Select(p => p.GetProperty("id").GetString()).ToList();
        Assert.Contains("finance.bill", ids);
        Assert.Contains("finance.budgets", ids);
        Assert.Contains("finance.recurring", ids);
        Assert.Contains("finance.approvals", ids); // the platform's "{moduleId}.approvals"
    }

    [Fact]
    public async Task DailyDigest_BatchesOverBudgetAndNewCharges_IntoTheInbox()
    {
        var (scope, tenantId, userId) = await fixture.AuthorizedScopeAsync();
        using var _ = scope;
        var db = scope.ServiceProvider.GetRequiredService<FinanceDbContext>();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var period = new DateOnly(today.Year, today.Month, 1);
        var category = await db.Categories.FirstAsync();

        var account = new Account
        {
            TenantId = tenantId,
            Name = "Digest checking",
            Type = "checking",
            CurrencyCode = "USD",
        };
        db.Accounts.Add(account);

        // One category pushed over its monthly budget…
        db.Budgets.Add(new Budget
        {
            TenantId = tenantId, CategoryId = category.Id, TargetAmount = 50,
            CurrencyCode = "USD", PeriodMonth = period,
        });
        db.Transactions.Add(new Transaction
        {
            TenantId = tenantId, AccountId = account.Id, CategoryId = category.Id,
            Amount = 80, CurrencyCode = "USD", Direction = "expense",
            Description = "digest overspend", Source = "manual",
            OccurredOn = period, CreatedByUserId = userId,
        });

        // …and a merchant whose THIRD steady charge lands today — newly detectable.
        foreach (var daysAgo in new[] { 60, 30, 0 })
        {
            db.Transactions.Add(new Transaction
            {
                TenantId = tenantId, AccountId = account.Id, CategoryId = category.Id,
                Amount = 199, CurrencyCode = "USD", Direction = "expense",
                Description = "Streamify subscription", Source = "manual",
                OccurredOn = today.AddDays(-daysAgo), CreatedByUserId = userId,
            });
        }

        await db.SaveChangesAsync();

        // Run the digest the way the scheduler does: the registered handler, a tenant-scoped
        // context, the platform's system user id (no human behind the run).
        var handler = scope.ServiceProvider.GetServices<IJobHandler>()
            .Single(h => h.Kind == DailyDigestJobHandler.JobKind);
        var result = await handler.ExecuteAsync(new JobExecutionContext
        {
            JobId = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = Guid.Empty,
            ModuleId = FinanceModule.Id,
            ArgumentsJson = "{}",
            ScopedServices = scope.ServiceProvider,
            ReportProgressAsync = (_, _, _) => Task.CompletedTask,
        }, CancellationToken.None);

        Assert.NotNull(result);

        // The member's inbox: one batched notification per category, each linking to its tab.
        using var client = fixture.AdminClient();
        var inbox = await client.GetFromJsonAsync<JsonElement>("/api/notifications/");
        var entries = inbox.EnumerateArray().ToList();

        var budgetAlert = entries.First(n => n.GetProperty("category").GetString() == "finance.budgets");
        Assert.Contains("over budget", budgetAlert.GetProperty("title").GetString());
        Assert.Equal("/finance/budgets", budgetAlert.GetProperty("link").GetString());

        var chargeAlert = entries.First(n => n.GetProperty("category").GetString() == "finance.recurring");
        Assert.Contains("Streamify", chargeAlert.GetProperty("body").GetString());
        Assert.Equal("/finance/recurring", chargeAlert.GetProperty("link").GetString());

        // Batched means batched: one notification per category from this run, not one per event.
        Assert.Single(entries, n => n.GetProperty("category").GetString() == "finance.budgets");
        Assert.Single(entries, n => n.GetProperty("category").GetString() == "finance.recurring");
    }
}

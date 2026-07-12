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
/// The recurring statement reminder (#71) end to end: the manifest's new category reaches the mute
/// switchboard, and one real run of the handler — executed exactly the way the platform scheduler
/// runs it, through the registered IJobHandler with a tenant-scoped context — lands a single
/// actionable nudge in the member's inbox. Steady declared income turns that nudge into a one-click
/// confirm, and a same-period catch-up run adds no duplicate.
/// </summary>
[Collection("api")]
public sealed class StatementReminderJobTests(IntegrationFixture fixture)
{
    private static async Task<string?> RunReminderAsync(IServiceProvider services, Guid tenantId) =>
        await services.GetServices<IJobHandler>()
            .Single(h => h.Kind == StatementReminderJobHandler.JobKind)
            .ExecuteAsync(new JobExecutionContext
            {
                JobId = Guid.NewGuid(),
                TenantId = tenantId,
                UserId = Guid.Empty, // the platform's system user — no human behind the run
                ModuleId = FinanceModule.Id,
                ArgumentsJson = "{}",
                ScopedServices = services,
                ReportProgressAsync = (_, _, _) => Task.CompletedTask,
            }, CancellationToken.None);

    [Fact]
    public async Task StatementCategory_ReachesTheMuteSwitchboard()
    {
        using var client = fixture.AdminClient();
        var preferences = await client.GetFromJsonAsync<JsonElement>("/api/notifications/preferences");
        var ids = preferences.EnumerateArray().Select(p => p.GetProperty("id").GetString()).ToList();
        Assert.Contains("finance.statements", ids);
    }

    [Fact]
    public async Task ConsistentIncome_OffersConfirm_AndIsIdempotentWithinThePeriod()
    {
        var (scope, tenantId, userId) = await fixture.AuthorizedScopeAsync();
        using var _ = scope;
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<FinanceDbContext>();

        // A clean, deterministic footing for this shared-collection tenant: reminders on, monthly, USD.
        await services.GetRequiredService<HouseholdSettingsTools>()
            .UpdateHouseholdSettings(defaultCurrency: "USD", timeZone: "UTC",
                statementReminders: true, statementReminderCadence: "monthly");

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var period = new DateOnly(today.Year, today.Month, 1);
        var lastMonth = period.AddMonths(-1).AddDays(14); // mid-previous-month, safely in range

        var account = new Account
        {
            TenantId = tenantId, Name = "Reminder checking", Type = "checking", CurrencyCode = "USD",
        };
        db.Accounts.Add(account);

        // A dominant declared income and a matching previous-month inflow: the household's income is
        // steady, so the nudge should offer a confirm rather than a hard upload. The amount dwarfs
        // any income other tests in the collection declared, keeping the ratio inside tolerance.
        db.IncomeSources.Add(new IncomeSource
        {
            TenantId = tenantId, Name = "Reminder payroll", Amount = 500_000m, CurrencyCode = "USD",
            Cadence = "monthly", AccountId = account.Id, CreatedByUserId = userId,
        });
        db.Transactions.Add(new Transaction
        {
            TenantId = tenantId, AccountId = account.Id, Amount = 500_000m, CurrencyCode = "USD",
            Direction = "income", Description = "Reminder payroll", Source = "manual",
            OccurredOn = lastMonth, CreatedByUserId = userId,
        });

        // The real platform scheduler runs this same job over the shared host's lifetime, so this
        // period may already be marked. Clear that marker for a deterministic controlled run — the
        // production idempotency guarantee is exercised by the second run below.
        db.StatementReminders.RemoveRange(
            await db.StatementReminders.Where(r => r.PeriodStart == period).ToListAsync());
        await db.SaveChangesAsync();

        // Run it the way the scheduler does: the registered handler, tenant-scoped context.
        var result = await RunReminderAsync(services, tenantId);
        Assert.NotNull(result);
        using (var doc = JsonDocument.Parse(result!))
        {
            // Steady income routed at least this member down the auto-confirm path.
            Assert.True(doc.RootElement.GetProperty("autoConfirm").GetInt32() >= 1);
        }

        // The confirm nudge is in the member's inbox: the one-click confirm path, not the upload gate.
        // (Contains, not Single — a prior scheduler run may have left this member an earlier nudge.)
        using var client = fixture.AdminClient();
        var inbox = await client.GetFromJsonAsync<JsonElement>("/api/notifications/");
        Assert.Contains(inbox.EnumerateArray(), n =>
            n.GetProperty("category").GetString() == "finance.statements" &&
            n.GetProperty("link").GetString() == "/finance/income" &&
            n.GetProperty("title").GetString()!.Contains("Confirm"));

        // A marker for this period now exists, so a same-period catch-up run adds no duplicate.
        Assert.True(await db.StatementReminders.AnyAsync(r => r.PeriodStart == period));
        var second = await RunReminderAsync(services, tenantId);
        Assert.Contains("alreadyNudged", second!);
    }
}

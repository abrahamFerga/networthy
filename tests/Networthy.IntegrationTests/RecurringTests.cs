using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Networthy.Finance;
using Networthy.Finance.Persistence;
using Xunit;

namespace Networthy.IntegrationTests;

/// <summary>
/// Recurring detection over the real ledger, and the reminder sweep's contract: due-soon
/// charges notify the member who logged them, exactly once per (merchant, expected date).
/// </summary>
[Collection("api")]
public sealed class RecurringTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task Subscriptions_AreDetected_AndListed_InChatAndTab()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var services = scope.ServiceProvider;

        await services.GetRequiredService<AccountTools>().CreateAccount("Recurring Checking", "checking", "USD", 500);
        var transactions = services.GetRequiredService<TransactionTools>();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var monthsAgo in new[] { 3, 2, 1 })
        {
            await transactions.LogOwnTransaction(
                "Recurring Checking", 15.49, "NETFLIX.COM 880-1234",
                date: today.AddMonths(-monthsAgo).ToString("yyyy-MM-dd"));
        }

        var listing = await services.GetRequiredService<RecurringTools>().ListRecurring();
        Assert.Contains("NETFLIX.COM", listing);
        Assert.Contains("monthly", listing);
        Assert.Contains("15.49", listing);

        using var client = fixture.AdminClient();
        var rows = await client.GetFromJsonAsync<JsonElement>("/api/finance/recurring");
        var netflix = rows.EnumerateArray().Single(r => r.GetProperty("name").GetString()!.Contains("NETFLIX"));
        Assert.Equal("monthly", netflix.GetProperty("cadence").GetString());
        Assert.Equal("steady", netflix.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ReminderSweep_NotifiesOnce_ForChargesDueSoon()
    {
        var (scope, tenantId, userId) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var services = scope.ServiceProvider;

        // A monthly rhythm in exact 30-day steps: next expected = today, squarely in the window
        // regardless of which calendar month the suite runs in.
        await services.GetRequiredService<AccountTools>().CreateAccount("Reminder Checking", "checking", "USD", 100);
        var transactions = services.GetRequiredService<TransactionTools>();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var daysAgo in new[] { 90, 60, 30 })
        {
            await transactions.LogOwnTransaction(
                "Reminder Checking", 60, "GYM MEMBERSHIP DUES",
                date: today.AddDays(-daysAgo).ToString("yyyy-MM-dd"));
        }

        var first = await BillReminderService.SweepOnceAsync(services);
        Assert.True(first >= 1, "expected at least the gym reminder to send");

        // Idempotent: the same sweep again sends nothing new for the same expected dates.
        var second = await BillReminderService.SweepOnceAsync(services);
        Assert.Equal(0, second);

        var reminder = await services.GetRequiredService<FinanceDbContext>()
            .BillReminders.IgnoreQueryFilters()
            .Where(r => r.TenantId == tenantId)
            .OrderByDescending(r => r.CreatedAt)
            .FirstAsync();
        Assert.Contains("GYM", reminder.MerchantKey);

        // The heads-up persisted to the recipient's in-app inbox (asserted at the store, since
        // the recipient is whichever household member logged the charges).
        var platform = services.GetRequiredService<Plenipo.Infrastructure.Persistence.PlatformDbContext>();
        var note = await platform.UserNotifications.IgnoreQueryFilters()
            .Where(n => n.TenantId == tenantId && n.Title.Contains("GYM MEMBERSHIP"))
            .FirstAsync();
        Assert.Equal(userId, note.UserId);
        Assert.Contains("60.00 monthly", note.Body);
    }
}

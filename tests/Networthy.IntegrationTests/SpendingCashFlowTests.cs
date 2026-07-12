using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Networthy.Finance;
using Xunit;

namespace Networthy.IntegrationTests;

/// <summary>
/// The spending & cash-flow visualization reads (issue #46), asserted at the shape level the
/// charts consume: the cash-flow bars' complete zero-filled window, the spending donut's
/// month-addressable category rows, and the bills calendar's composed 60-day payload.
/// </summary>
[Collection("api")]
public sealed class SpendingCashFlowTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task CashFlow_EmitsTheFullZeroFilledWindow_CountingALoggedTransaction()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var services = scope.ServiceProvider;

        await services.GetRequiredService<AccountTools>().CreateAccount("CashFlow Checking", "checking", "USD", 100);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await services.GetRequiredService<TransactionTools>().LogOwnTransaction(
            "CashFlow Checking", 123.45, "CASHFLOW PROBE EXPENSE", date: today.ToString("yyyy-MM-dd"));

        using var client = fixture.AdminClient();
        var rows = await client.GetFromJsonAsync<JsonElement>("/api/finance/cashflow");

        // With activity in the window: 12 months × income + expense, quiet months zero-filled.
        var list = rows.EnumerateArray().ToList();
        Assert.Equal(24, list.Count);
        Assert.All(list, r =>
        {
            Assert.Matches(@"^\d{4}-\d{2}$", r.GetProperty("month").GetString());
            Assert.Contains(r.GetProperty("direction").GetString(), new[] { "income", "expense" });
            Assert.True(r.GetProperty("amount").GetDecimal() >= 0, "amounts are absolute; direction carries the sign");
        });

        // Oldest month first — the bar chart renders categories in row order.
        var months = list.Select(r => r.GetProperty("month").GetString()).Distinct().ToList();
        Assert.Equal(12, months.Count);
        Assert.Equal(months.OrderBy(m => m), months);

        // Other suite tests spend in this month too, so the bucket holds AT LEAST the probe.
        var currentExpense = list.Single(r =>
            r.GetProperty("month").GetString() == today.ToString("yyyy-MM") &&
            r.GetProperty("direction").GetString() == "expense");
        Assert.True(currentExpense.GetProperty("amount").GetDecimal() >= 123.45m);
    }

    [Fact]
    public async Task Spending_SummarizesTheMonth_PerCategory()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var services = scope.ServiceProvider;

        await services.GetRequiredService<AccountTools>().CreateAccount("Spending Checking", "checking", "USD", 100);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await services.GetRequiredService<TransactionTools>().LogOwnTransaction(
            "Spending Checking", 55, "SPENDING PROBE GROCERIES", date: today.ToString("yyyy-MM-dd"),
            category: "Groceries");

        using var client = fixture.AdminClient();
        var rows = await client.GetFromJsonAsync<JsonElement>(
            $"/api/finance/spending?month={today:yyyy-MM}");

        var groceries = rows.EnumerateArray().Single(r => r.GetProperty("category").GetString() == "Groceries");
        Assert.True(groceries.GetProperty("amount").GetDecimal() >= 55m);
        Assert.True(groceries.GetProperty("count").GetInt32() >= 1);

        // The default (no ?month=) is the household's current month — same rows.
        var defaulted = await client.GetFromJsonAsync<JsonElement>("/api/finance/spending");
        Assert.Contains("Groceries",
            defaulted.EnumerateArray().Select(r => r.GetProperty("category").GetString()));
    }

    [Fact]
    public async Task Spending_RejectsAnUnparseableMonth()
    {
        using var client = fixture.AdminClient();
        var response = await client.GetAsync("/api/finance/spending?month=not-a-month");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UpcomingBills_ComposeTheSixtyDayCalendarWindow()
    {
        using var client = fixture.AdminClient();
        var payload = await client.GetFromJsonAsync<JsonElement>("/api/finance/bills/upcoming");

        var today = DateOnly.Parse(payload.GetProperty("today").GetString()!);
        var until = DateOnly.Parse(payload.GetProperty("until").GetString()!);
        Assert.Equal(60, until.DayNumber - today.DayNumber);
        Assert.False(string.IsNullOrEmpty(payload.GetProperty("currencyCode").GetString()));

        // Every projected bill sits inside the window (other tests may have seeded charges).
        Assert.All(payload.GetProperty("bills").EnumerateArray(), b =>
        {
            var dueOn = DateOnly.Parse(b.GetProperty("dueOn").GetString()!);
            Assert.InRange(dueOn, today, until);
            Assert.False(string.IsNullOrEmpty(b.GetProperty("name").GetString()));
            Assert.True(b.GetProperty("amount").GetDecimal() > 0);
        });
    }
}

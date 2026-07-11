using System.Text.RegularExpressions;
using Cortex.Application.Files;
using Microsoft.Extensions.DependencyInjection;
using Networthy.Finance;
using Xunit;

namespace Networthy.IntegrationTests;

/// <summary>
/// Reports and exports through the real moving parts: the tools build the file, the platform
/// file store keeps it, and the message hands back the download path. The CSV content check
/// resolves the stored file through the SAME <see cref="IFileStore"/> the tool used — proving
/// the bytes a user downloads are the bytes the tool wrote. The collection shares one tenant,
/// so account names here are distinctive on purpose.
/// </summary>
[Collection("api")]
public sealed class ExportToolsTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task ExportTransactions_WritesTheVisibleRows_ToAStoredCsv()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var services = scope.ServiceProvider;

        await services.GetRequiredService<AccountTools>()
            .CreateAccount("Export Probe Checking", "checking", "USD", 500);
        var transactions = services.GetRequiredService<TransactionTools>();
        await transactions.LogOwnTransaction("Export Probe Checking", 12.34, "export-probe tacos, extra salsa");
        await transactions.LogOwnTransaction("Export Probe Checking", 900, "export-probe payroll", direction: "income");

        var message = await services.GetRequiredService<ExportTools>().ExportTransactions();
        Assert.Contains(".csv", message);
        Assert.Contains("Download: /api/files/", message);

        // Resolve the stored file through the same service the tool used and read it back.
        var fileId = Guid.Parse(Regex.Match(message, @"File id: ([0-9a-f-]{36})").Groups[1].Value);
        await using var content = await services.GetRequiredService<IFileStore>().OpenReadAsync(fileId);
        Assert.NotNull(content);
        using var reader = new StreamReader(content!);
        var csv = await reader.ReadToEndAsync();

        Assert.StartsWith("date,account,category,direction,amount,currency,description", csv);
        // The comma-bearing description survives as ONE quoted field on its row.
        Assert.Contains("\"export-probe tacos, extra salsa\"", csv);
        Assert.Contains("export-probe payroll", csv);
        Assert.Contains("Export Probe Checking", csv);
        Assert.Contains("income,900.00,USD", csv);
    }

    [Fact]
    public async Task ExportTransactions_RefusesGarbageDates()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;

        var message = await scope.ServiceProvider.GetRequiredService<ExportTools>()
            .ExportTransactions(fromDate: "not-a-date");
        Assert.Contains("not a date", message);
    }

    [Fact]
    public async Task GenerateMonthlyReport_StoresAPdf()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var services = scope.ServiceProvider;

        await services.GetRequiredService<AccountTools>()
            .CreateAccount("Report Probe Savings", "savings", "USD", 1500);
        // Deliberately uncategorized: the collection shares one tenant, and other tests pin
        // exact per-category sums (FullJourneyTests' Groceries math) — don't disturb them.
        await services.GetRequiredService<TransactionTools>()
            .LogOwnTransaction("Report Probe Savings", 55, "report-probe supplies");

        var message = await services.GetRequiredService<ExportTools>().GenerateMonthlyReport();
        Assert.Contains(".pdf", message);
        Assert.Contains("Download: /api/files/", message);

        // The stored bytes are a real PDF (header magic), not an empty shell.
        var fileId = Guid.Parse(Regex.Match(message, @"File id: ([0-9a-f-]{36})").Groups[1].Value);
        await using var content = await services.GetRequiredService<IFileStore>().OpenReadAsync(fileId);
        Assert.NotNull(content);
        var header = new byte[5];
        await content!.ReadExactlyAsync(header);
        Assert.Equal("%PDF-"u8.ToArray(), header);
    }

    [Fact]
    public async Task ExportActivityLog_StoresACsvOfRecentEvents()
    {
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var services = scope.ServiceProvider;

        await services.GetRequiredService<AccountTools>()
            .CreateAccount("Activity Probe Cash", "cash", "USD", 40);
        await services.GetRequiredService<TransactionTools>()
            .LogOwnTransaction("Activity Probe Cash", 4, "activity-probe coffee");

        var message = await services.GetRequiredService<ExportTools>().ExportActivityLog();
        Assert.Contains(".csv", message);
        Assert.Contains("Download: /api/files/", message);

        var fileId = Guid.Parse(Regex.Match(message, @"File id: ([0-9a-f-]{36})").Groups[1].Value);
        await using var content = await services.GetRequiredService<IFileStore>().OpenReadAsync(fileId);
        Assert.NotNull(content);
        using var reader = new StreamReader(content!);
        var csv = await reader.ReadToEndAsync();

        Assert.StartsWith("timestamp,event,direction,amount,currency,description,account", csv);
        Assert.Contains("activity-probe coffee", csv);
        Assert.Contains("account created", csv);
        Assert.Contains("Activity Probe Cash", csv);
    }
}

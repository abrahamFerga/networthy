using System.ComponentModel;
using System.Globalization;
using System.Text;
using Plenipo.Application.Documents;
using Plenipo.Application.Files;
using Plenipo.Core.Identity;
using Microsoft.EntityFrameworkCore;
using Networthy.Finance.Persistence;

namespace Networthy.Finance;

/// <summary>
/// Reports and exports: the household's own data, handed back as files. A CSV of transactions
/// for spreadsheets and accountants, a one-page PDF month summary, and a CSV of the activity
/// log. Everything goes through the platform file store and comes back as a download link;
/// nothing here changes a record, so none of these tools are approval-gated — but every export
/// honors per-member account visibility just like the read tools it mirrors.
/// </summary>
public sealed class ExportTools(
    FinanceDbContext db,
    IFileStore files,
    IPdfRenderer pdf,
    ICurrentUser currentUser,
    HouseholdContext household)
{
    [Description("Export the caller-visible transactions as a downloadable CSV file (date, account, category, direction, amount, currency, description), optionally limited to a date range. Read-only: creates a file, changes no records.")]
    public async Task<string> ExportTransactions(
        [Description("Optional period start, ISO date inclusive (yyyy-MM-dd).")] string? fromDate = null,
        [Description("Optional period end, ISO date inclusive (yyyy-MM-dd).")] string? toDate = null,
        CancellationToken cancellationToken = default)
    {
        DateOnly? from = null;
        if (!string.IsNullOrWhiteSpace(fromDate))
        {
            if (!DateOnly.TryParse(fromDate, CultureInfo.InvariantCulture, out var parsed))
            {
                return $"'{fromDate}' is not a date I can parse — use an ISO date like 2026-07-01, or omit it.";
            }

            from = parsed;
        }

        DateOnly? to = null;
        if (!string.IsNullOrWhiteSpace(toDate))
        {
            if (!DateOnly.TryParse(toDate, CultureInfo.InvariantCulture, out var parsed))
            {
                return $"'{toDate}' is not a date I can parse — use an ISO date like 2026-07-31, or omit it.";
            }

            to = parsed;
        }

        var query = db.Transactions.AsQueryable();
        if (from is { } f)
        {
            query = query.Where(t => t.OccurredOn >= f);
        }

        if (to is { } t)
        {
            query = query.Where(x => x.OccurredOn <= t);
        }

        var visibleAccounts = (await db.Accounts.ToListAsync(cancellationToken))
            .Where(a => a.IsVisibleTo(currentUser.UserId))
            .ToDictionary(a => a.Id, a => a.Name);
        var categories = await db.Categories.ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);

        var rows = (await query.OrderBy(x => x.OccurredOn).ThenBy(x => x.CreatedAt).ToListAsync(cancellationToken))
            .Where(x => visibleAccounts.ContainsKey(x.AccountId))
            .ToList();
        if (rows.Count == 0)
        {
            return "No transactions to export" +
                   (from is null && to is null ? "" : " in that period") +
                   ". Log some first, or widen the date range.";
        }

        var csv = ExportMath.BuildTransactionsCsv(rows.Select(x => new ExportMath.TransactionRow(
            x.OccurredOn,
            visibleAccounts[x.AccountId],
            x.CategoryId is { } c && categories.TryGetValue(c, out var name) ? name : "",
            x.Direction,
            x.Amount,
            x.CurrencyCode,
            x.Description)));

        var range = (from, to) switch
        {
            ({ } f2, { } t2) => $"{f2:yyyy-MM-dd}-to-{t2:yyyy-MM-dd}",
            ({ } f2, null) => $"from-{f2:yyyy-MM-dd}",
            (null, { } t2) => $"to-{t2:yyyy-MM-dd}",
            _ => $"all-{await household.TodayAsync(cancellationToken):yyyy-MM-dd}",
        };
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var stored = await files.SaveAsync(
            $"transactions-{range}.csv", "text/csv", stream, source: "export_transactions", cancellationToken);

        return $"Exported {rows.Count} transaction(s) to '{stored.FileName}' ({stored.SizeBytes:N0} bytes). " +
               $"File id: {stored.Id}. Download: /api/files/{stored.Id}";
    }

    [Description("Generate a downloadable PDF summary of a month: income and expense totals, top expense categories, per-currency net worth, and budget statuses. Defaults to the current month. Read-only: creates a file, changes no records.")]
    public async Task<string> GenerateMonthlyReport(
        [Description("The month as yyyy-MM (default: the household's current month).")] string? month = null,
        CancellationToken cancellationToken = default)
    {
        if (!BudgetMath.TryParseMonth(month, await household.TodayAsync(cancellationToken), out var period))
        {
            return $"'{month}' is not a month I can parse — use yyyy-MM, e.g. 2026-07.";
        }

        var monthEnd = period.AddMonths(1).AddDays(-1);
        var visibleAccounts = (await db.Accounts.ToListAsync(cancellationToken))
            .Where(a => a.IsVisibleTo(currentUser.UserId))
            .ToList();
        var visibleIds = visibleAccounts.Select(a => a.Id).ToHashSet();
        var monthRows = (await db.Transactions
                .Where(t => t.OccurredOn >= period && t.OccurredOn <= monthEnd)
                .ToListAsync(cancellationToken))
            .Where(t => visibleIds.Contains(t.AccountId))
            .ToList();
        var categories = await db.Categories.ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);

        // BuildPdf treats "\n\n" as the paragraph break (single newlines collapse into the same
        // wrapped paragraph), so every report line is its own paragraph.
        var body = new StringBuilder();
        body.Append($"Money in and out, {period:yyyy-MM-dd} to {monthEnd:yyyy-MM-dd}.");

        if (monthRows.Count == 0)
        {
            body.Append("\n\nNo transactions recorded this month.");
        }
        else
        {
            foreach (var group in monthRows
                         .GroupBy(t => t.CurrencyCode.ToUpperInvariant())
                         .OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var income = group.Where(t => t.Direction == "income").Sum(t => t.Amount);
                var expense = group.Where(t => t.Direction == "expense").Sum(t => t.Amount);
                body.Append($"\n\n{group.Key}: income {income:N2}, expenses {expense:N2}, net {income - expense:N2}.");
            }

            var topExpenses = SpendingMath.SummarizeByCategory(
                monthRows.Where(t => t.Direction == "expense"), categories).Take(5).ToList();
            if (topExpenses.Count > 0)
            {
                body.Append("\n\nTop expense categories:");
                foreach (var line in topExpenses)
                {
                    body.Append($"\n\n- {line.Category}: {line.Total:N2} {line.Currency} ({line.Count} transaction(s))");
                }
            }
        }

        body.Append("\n\nNet worth (current balances, per currency):");
        if (visibleAccounts.Count == 0)
        {
            body.Append("\n\n- No accounts yet.");
        }
        else
        {
            foreach (var (currency, total) in NetWorthMath.SumByCurrency(visibleAccounts))
            {
                body.Append($"\n\n- {total:N2} {currency}");
            }
        }

        var budgets = await db.Budgets.Where(b => b.PeriodMonth == period).ToListAsync(cancellationToken);
        if (budgets.Count > 0)
        {
            body.Append("\n\nBudgets:");
            foreach (var budget in budgets.OrderBy(b => categories.GetValueOrDefault(b.CategoryId, "?")))
            {
                var spent = monthRows
                    .Where(t => t.Direction == "expense" && t.CategoryId == budget.CategoryId &&
                                t.CurrencyCode.Equals(budget.CurrencyCode, StringComparison.OrdinalIgnoreCase))
                    .Sum(t => t.Amount);
                var status = BudgetMath.Status(budget.TargetAmount, spent);
                var name = categories.GetValueOrDefault(budget.CategoryId, "(deleted category)");
                body.Append($"\n\n- {name}: {spent:N2} / {budget.TargetAmount:N2} {budget.CurrencyCode}" +
                            (status.Over ? $" — OVER by {-status.Remaining:N2}" : $" — {status.Remaining:N2} left"));
            }
        }

        var title = $"Networthy monthly report — {period:yyyy-MM}";
        var bytes = pdf.Render(title, body.ToString());
        using var stream = new MemoryStream(bytes);
        var stored = await files.SaveAsync(
            $"monthly-report-{period:yyyy-MM}.pdf", "application/pdf", stream,
            source: "generate_monthly_report", cancellationToken);

        return $"Generated the {period:yyyy-MM} report as '{stored.FileName}' ({stored.SizeBytes:N0} bytes). " +
               $"File id: {stored.Id}. Download: /api/files/{stored.Id}";
    }

    [Description("Export the household's recent activity (what get_activity_log reports) as a downloadable CSV file. Read-only: creates a file, changes no records.")]
    public async Task<string> ExportActivityLog(
        [Description("How many days back (default 30, max 90).")] int days = 30,
        CancellationToken cancellationToken = default)
    {
        var clamped = Math.Clamp(days, 1, 90);
        var since = DateTimeOffset.UtcNow.AddDays(-clamped);

        var accounts = await db.Accounts.ToDictionaryAsync(a => a.Id, a => a.Name, cancellationToken);
        var visible = (await db.Accounts.ToListAsync(cancellationToken))
            .Where(a => a.IsVisibleTo(currentUser.UserId))
            .Select(a => a.Id)
            .ToHashSet();

        // The same event sources get_activity_log narrates — flattened to rows instead of prose.
        var events = new List<(DateTimeOffset At, string[] Row)>();

        foreach (var t in (await db.Transactions.Where(t => t.CreatedAt >= since).ToListAsync(cancellationToken))
                 .Where(t => visible.Contains(t.AccountId)))
        {
            events.Add((t.CreatedAt,
            [
                ExportMath.Timestamp(t.CreatedAt), $"transaction ({t.Source})", t.Direction,
                t.Amount.ToString("0.00", CultureInfo.InvariantCulture), t.CurrencyCode,
                t.Description, accounts.GetValueOrDefault(t.AccountId, "?"),
            ]));
        }

        foreach (var b in await db.ImportBatches
                     .Where(b => b.CreatedAt >= since && visible.Contains(b.AccountId))
                     .ToListAsync(cancellationToken))
        {
            events.Add((b.CreatedAt,
            [
                ExportMath.Timestamp(b.CreatedAt), "statement import", "", "", "",
                $"'{b.FileName}' — {b.Status}" + (b.ReviewedAt is { } r ? $", reviewed {ExportMath.Timestamp(r)}" : ""), "",
            ]));
        }

        foreach (var budget in await db.Budgets.Where(b => b.CreatedAt >= since).ToListAsync(cancellationToken))
        {
            events.Add((budget.CreatedAt,
            [
                ExportMath.Timestamp(budget.CreatedAt), "budget set", "",
                budget.TargetAmount.ToString("0.00", CultureInfo.InvariantCulture), budget.CurrencyCode,
                $"target for {budget.PeriodMonth:yyyy-MM}", "",
            ]));
        }

        foreach (var a in (await db.Accounts.Where(a => a.CreatedAt >= since).ToListAsync(cancellationToken))
                 .Where(a => visible.Contains(a.Id)))
        {
            events.Add((a.CreatedAt,
            [
                ExportMath.Timestamp(a.CreatedAt), "account created", "", "", "",
                $"{a.Name} [{a.Type}]", a.Name,
            ]));
        }

        if (events.Count == 0)
        {
            return $"No activity in the last {clamped} day(s) — nothing to export.";
        }

        var csv = ExportMath.BuildCsv(
            ["timestamp", "event", "direction", "amount", "currency", "description", "account"],
            events.OrderByDescending(e => e.At).Select(e => (IReadOnlyList<string>)e.Row));

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var stored = await files.SaveAsync(
            $"activity-log-last-{clamped}-days.csv", "text/csv", stream,
            source: "export_activity_log", cancellationToken);

        return $"Exported {events.Count} activity event(s) from the last {clamped} day(s) to " +
               $"'{stored.FileName}' ({stored.SizeBytes:N0} bytes). File id: {stored.Id}. Download: /api/files/{stored.Id}";
    }
}

/// <summary>Pure CSV-building logic, unit-tested without a database.</summary>
public static class ExportMath
{
    /// <summary>One CSV line of the transactions export, already resolved to display strings.</summary>
    public sealed record TransactionRow(
        DateOnly Date, string Account, string Category, string Direction,
        decimal Amount, string Currency, string Description);

    /// <summary>Spreadsheet-safe RFC-4180 escaping. User-controlled values that could be
    /// interpreted as formulas are prefixed with an apostrophe before normal CSV quoting.</summary>
    public static string EscapeCsvField(string field)
    {
        var firstNonSpace = field.AsSpan().TrimStart(' ');
        var safe = firstNonSpace.Length > 0 && firstNonSpace[0] is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n'
            ? $"'{field}"
            : field;

        return safe.Contains(',') || safe.Contains('"') || safe.Contains('\n') || safe.Contains('\r')
            ? $"\"{safe.Replace("\"", "\"\"")}\""
            : safe;
    }

    /// <summary>A complete CSV document: header line, then one line per row, every field escaped.</summary>
    public static string BuildCsv(IReadOnlyList<string> header, IEnumerable<IReadOnlyList<string>> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', header.Select(EscapeCsvField)));
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(',', row.Select(EscapeCsvField)));
        }

        return sb.ToString();
    }

    /// <summary>The transactions export: fixed columns, invariant-culture amounts and dates.</summary>
    public static string BuildTransactionsCsv(IEnumerable<TransactionRow> rows) =>
        BuildCsv(
            ["date", "account", "category", "direction", "amount", "currency", "description"],
            rows.Select(r => (IReadOnlyList<string>)
            [
                r.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                r.Account,
                r.Category,
                r.Direction,
                r.Amount.ToString("0.00", CultureInfo.InvariantCulture),
                r.Currency,
                r.Description,
            ]));

    /// <summary>UTC timestamp formatting shared by the activity export's rows.</summary>
    public static string Timestamp(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

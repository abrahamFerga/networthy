using System.ComponentModel;
using System.Globalization;
using System.Text;
using Plenipo.Core.Identity;
using Plenipo.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Networthy.Finance.Persistence;

namespace Networthy.Finance;

/// <summary>
/// Transaction capture, categorization, search, and spending summaries.
/// <c>log_own_transaction</c> is the module's ONE deliberately ungated write (ADR-0005): a
/// member recording their own known-true spending shouldn't wait on approval — the gate exists
/// to catch AI mistakes, and AI-suggested categorization stays fully gated.
/// </summary>
public sealed class TransactionTools(
    FinanceDbContext db,
    ITenantContext tenant,
    ICurrentUser currentUser,
    HouseholdContext household)
{
    [Description("Log the CALLER'S OWN transaction (a purchase or income they know happened). Quick capture: not approval-gated — entries are correctable with edit_transaction.")]
    public async Task<string> LogOwnTransaction(
        [Description("The account name the money moved on.")] string accountName,
        [Description("The amount, always positive, e.g. 6.50.")] double amount,
        [Description("What it was — the narrative line, e.g. 'Coffee at Blue Bottle'.")] string description,
        [Description("expense (default) or income.")] string direction = "expense",
        [Description("The day it happened as an ISO date (default: today).")] string? date = null,
        [Description("Optional category name (must exist — see the Categories tab).")] string? category = null,
        CancellationToken cancellationToken = default)
    {
        var account = await FindVisibleAccountAsync(accountName, cancellationToken);
        if (account is null)
        {
            return $"No account named '{accountName}' exists (or it is private to another member). Use list_accounts.";
        }

        if (amount <= 0)
        {
            return "amount must be positive; use direction to say income vs expense.";
        }

        var normalizedDirection = Transaction.NormalizeDirection(direction);
        if (normalizedDirection is null)
        {
            return $"'{direction}' is not a direction. Use expense or income.";
        }

        var occurredOn = await household.TodayAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(date) && !DateOnly.TryParse(date, CultureInfo.InvariantCulture, out occurredOn))
        {
            return $"'{date}' is not a date I can parse — use an ISO date like 2026-07-09, or omit it for today.";
        }

        Guid? categoryId = null;
        if (!string.IsNullOrWhiteSpace(category))
        {
            var match = await db.Categories.FirstOrDefaultAsync(
                c => EF.Functions.ILike(c.Name, category.Trim()), cancellationToken);
            if (match is null)
            {
                return $"No category named '{category}' exists. Check the Categories tab, or omit it and categorize later.";
            }

            categoryId = match.Id;
        }

        var transaction = new Transaction
        {
            TenantId = tenant.RequireTenantId(),
            AccountId = account.Id,
            OccurredOn = occurredOn,
            Amount = (decimal)amount,
            CurrencyCode = account.CurrencyCode,
            Description = description.Trim(),
            CategoryId = categoryId,
            Direction = normalizedDirection,
            // "assistant", not "manual": this write happens through a chat tool, so the model
            // chose the arguments — the AI-origin tag (issue #49) must survive even on the
            // ungated quick-capture path. The human typing into a form is ManualCrudEndpoints.
            Source = "assistant",
            CreatedByUserId = currentUser.UserId,
        };
        db.Transactions.Add(transaction);
        account.CachedBalance += transaction.BalanceDelta;
        await db.SaveChangesAsync(cancellationToken);

        return $"Logged {normalizedDirection} of {transaction.Amount:N2} {transaction.CurrencyCode} on " +
               $"'{account.Name}' for {occurredOn:yyyy-MM-dd}: {transaction.Description}" +
               $"{(categoryId is null ? " (uncategorized — categorize_transaction can fix that)" : "")}. " +
               $"New balance: {account.CachedBalance:N2} {account.CurrencyCode}.";
    }

    [Description("Set or change a transaction's category (AI-suggested categorization lands through this). Side-effecting and requires approval.")]
    public async Task<string> CategorizeTransaction(
        [Description("The transaction's description (or a distinctive part of it), as shown by search_transactions.")] string descriptionMatch,
        [Description("The category name to assign (must exist).")] string category,
        [Description("Optional ISO date to disambiguate when several match.")] string? date = null,
        CancellationToken cancellationToken = default)
    {
        var categoryRow = await db.Categories.FirstOrDefaultAsync(
            c => EF.Functions.ILike(c.Name, category.Trim()), cancellationToken);
        if (categoryRow is null)
        {
            return $"No category named '{category}' exists. Check the Categories tab first.";
        }

        var pattern = $"%{descriptionMatch.Trim()}%";
        var query = db.Transactions.Where(t => EF.Functions.ILike(t.Description, pattern));
        if (!string.IsNullOrWhiteSpace(date) && DateOnly.TryParse(date, CultureInfo.InvariantCulture, out var day))
        {
            query = query.Where(t => t.OccurredOn == day);
        }

        var matches = await query.OrderByDescending(t => t.OccurredOn).Take(5).ToListAsync(cancellationToken);
        if (matches.Count == 0)
        {
            return $"No transaction matches '{descriptionMatch}'. Use search_transactions to find the right one.";
        }

        if (matches.Count > 1)
        {
            var sb = new StringBuilder($"{matches.Count} transactions match '{descriptionMatch}' — be more specific (or pass the date):\n");
            foreach (var m in matches)
            {
                sb.AppendLine($"- {m.OccurredOn:yyyy-MM-dd} · {m.Amount:N2} {m.CurrencyCode} · {m.Description}");
            }

            return sb.ToString();
        }

        var transaction = matches[0];
        transaction.CategoryId = categoryRow.Id;
        await db.SaveChangesAsync(cancellationToken);
        return $"Categorized '{transaction.Description}' ({transaction.OccurredOn:yyyy-MM-dd}, " +
               $"{transaction.Amount:N2} {transaction.CurrencyCode}) as {categoryRow.Name}.";
    }

    [Description("Correct a transaction's amount, description, or date. Side-effecting and requires approval; balances are adjusted accordingly.")]
    public async Task<string> EditTransaction(
        [Description("The transaction's current description (or a distinctive part), as shown by search_transactions.")] string descriptionMatch,
        [Description("Optional new amount (positive).")] double? newAmount = null,
        [Description("Optional new description.")] string? newDescription = null,
        [Description("Optional new ISO date.")] string? newDate = null,
        CancellationToken cancellationToken = default)
    {
        var pattern = $"%{descriptionMatch.Trim()}%";
        var matches = await db.Transactions
            .Where(t => EF.Functions.ILike(t.Description, pattern))
            .OrderByDescending(t => t.OccurredOn)
            .Take(5)
            .ToListAsync(cancellationToken);
        if (matches.Count != 1)
        {
            return matches.Count == 0
                ? $"No transaction matches '{descriptionMatch}'. Use search_transactions to find it."
                : $"{matches.Count} transactions match '{descriptionMatch}' — be more specific.";
        }

        var transaction = matches[0];
        if (newAmount is { } amount)
        {
            if (amount <= 0)
            {
                return "newAmount must be positive.";
            }

            var account = await db.Accounts.FirstAsync(a => a.Id == transaction.AccountId, cancellationToken);
            account.CachedBalance -= transaction.BalanceDelta;      // back out the old delta
            transaction.Amount = (decimal)amount;
            account.CachedBalance += transaction.BalanceDelta;      // apply the new one
        }

        if (!string.IsNullOrWhiteSpace(newDescription))
        {
            transaction.Description = newDescription.Trim();
        }

        if (!string.IsNullOrWhiteSpace(newDate))
        {
            if (!DateOnly.TryParse(newDate, CultureInfo.InvariantCulture, out var day))
            {
                return $"'{newDate}' is not a date I can parse — use an ISO date like 2026-07-09.";
            }

            transaction.OccurredOn = day;
        }

        await db.SaveChangesAsync(cancellationToken);
        return $"Updated: {transaction.OccurredOn:yyyy-MM-dd} · {transaction.Amount:N2} {transaction.CurrencyCode} · {transaction.Description}.";
    }

    [Description("Search transactions by text, category, and/or date range (newest first).")]
    public async Task<string> SearchTransactions(
        [Description("Optional text to match in the description.")] string? text = null,
        [Description("Optional category name to filter by.")] string? category = null,
        [Description("Optional period start, ISO date inclusive.")] string? fromDate = null,
        [Description("Optional period end, ISO date inclusive.")] string? toDate = null,
        CancellationToken cancellationToken = default)
    {
        var query = db.Transactions.AsQueryable();
        if (!string.IsNullOrWhiteSpace(text))
        {
            var pattern = $"%{text.Trim()}%";
            query = query.Where(t => EF.Functions.ILike(t.Description, pattern));
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            var categoryRow = await db.Categories.FirstOrDefaultAsync(
                c => EF.Functions.ILike(c.Name, category.Trim()), cancellationToken);
            if (categoryRow is null)
            {
                return $"No category named '{category}' exists.";
            }

            query = query.Where(t => t.CategoryId == categoryRow.Id);
        }

        if (!string.IsNullOrWhiteSpace(fromDate) && DateOnly.TryParse(fromDate, CultureInfo.InvariantCulture, out var from))
        {
            query = query.Where(t => t.OccurredOn >= from);
        }

        if (!string.IsNullOrWhiteSpace(toDate) && DateOnly.TryParse(toDate, CultureInfo.InvariantCulture, out var to))
        {
            query = query.Where(t => t.OccurredOn <= to);
        }

        var visibleAccounts = (await db.Accounts.ToListAsync(cancellationToken))
            .Where(a => a.IsVisibleTo(currentUser.UserId))
            .ToDictionary(a => a.Id, a => a.Name);

        var results = (await query.OrderByDescending(t => t.OccurredOn).Take(100).ToListAsync(cancellationToken))
            .Where(t => visibleAccounts.ContainsKey(t.AccountId))
            .Take(25)
            .ToList();
        if (results.Count == 0)
        {
            return "No transactions match. Log one with log_own_transaction, or import a statement.";
        }

        var categories = await db.Categories.ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        var sb = new StringBuilder("Transactions (newest first):\n");
        foreach (var t in results)
        {
            sb.AppendLine(
                $"- {t.OccurredOn:yyyy-MM-dd} · {(t.Direction == "income" ? "+" : "-")}{t.Amount:N2} {t.CurrencyCode} · " +
                $"{t.Description} — {visibleAccounts[t.AccountId]}" +
                $"{(t.CategoryId is { } c && categories.TryGetValue(c, out var name) ? $" [{name}]" : " [uncategorized]")}");
        }

        return sb.ToString();
    }

    [Description("Spending (or income) summed by category over a period — answers 'how much did we spend on X'. Defaults to the current month.")]
    public async Task<string> SummarizeSpending(
        [Description("Optional category name to focus on; omit for all categories.")] string? category = null,
        [Description("Period start, ISO date inclusive (default: first of the current month).")] string? fromDate = null,
        [Description("Period end, ISO date inclusive (default: today).")] string? toDate = null,
        [Description("expense (default) or income.")] string direction = "expense",
        CancellationToken cancellationToken = default)
    {
        var normalizedDirection = Transaction.NormalizeDirection(direction) ?? "expense";
        var today = await household.TodayAsync(cancellationToken);
        var from = new DateOnly(today.Year, today.Month, 1);
        if (!string.IsNullOrWhiteSpace(fromDate) && !DateOnly.TryParse(fromDate, CultureInfo.InvariantCulture, out from))
        {
            return $"'{fromDate}' is not a date I can parse — use an ISO date like 2026-07-01.";
        }

        var to = today;
        if (!string.IsNullOrWhiteSpace(toDate) && !DateOnly.TryParse(toDate, CultureInfo.InvariantCulture, out to))
        {
            return $"'{toDate}' is not a date I can parse — use an ISO date like 2026-07-31.";
        }

        Guid? categoryId = null;
        if (!string.IsNullOrWhiteSpace(category))
        {
            var categoryRow = await db.Categories.FirstOrDefaultAsync(
                c => EF.Functions.ILike(c.Name, category.Trim()), cancellationToken);
            if (categoryRow is null)
            {
                return $"No category named '{category}' exists.";
            }

            categoryId = categoryRow.Id;
        }

        var visibleAccountIds = (await db.Accounts.ToListAsync(cancellationToken))
            .Where(a => a.IsVisibleTo(currentUser.UserId))
            .Select(a => a.Id)
            .ToHashSet();

        var rows = (await db.Transactions
                .Where(t => t.Direction == normalizedDirection && t.OccurredOn >= from && t.OccurredOn <= to)
                .Where(t => categoryId == null || t.CategoryId == categoryId)
                .ToListAsync(cancellationToken))
            .Where(t => visibleAccountIds.Contains(t.AccountId))
            .ToList();
        if (rows.Count == 0)
        {
            return $"No {normalizedDirection} recorded between {from:yyyy-MM-dd} and {to:yyyy-MM-dd}.";
        }

        var categories = await db.Categories.ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        var summary = SpendingMath.SummarizeByCategory(rows, categories);
        var sb = new StringBuilder(
            $"{(normalizedDirection == "income" ? "Income" : "Spending")} {from:yyyy-MM-dd} – {to:yyyy-MM-dd}:\n");
        foreach (var line in summary)
        {
            sb.AppendLine($"- {line.Category}: {line.Total:N2} {line.Currency} ({line.Count} transaction(s))");
        }

        return sb.ToString();
    }

    private async Task<Account?> FindVisibleAccountAsync(string name, CancellationToken cancellationToken)
    {
        var normalized = name.Trim();
        var account = await db.Accounts.FirstOrDefaultAsync(
            a => EF.Functions.ILike(a.Name, normalized), cancellationToken);
        return account is not null && account.IsVisibleTo(currentUser.UserId) ? account : null;
    }
}

/// <summary>Pure spending-summary math, unit-tested without a database.</summary>
public static class SpendingMath
{
    public sealed record CategoryLine(string Category, decimal Total, string Currency, int Count);

    /// <summary>Sums per category+currency, largest totals first; uncategorized rows group under
    /// "(uncategorized)".</summary>
    public static IReadOnlyList<CategoryLine> SummarizeByCategory(
        IEnumerable<Transaction> transactions, IReadOnlyDictionary<Guid, string> categoryNames) =>
        [.. transactions
            .GroupBy(t => (
                Category: t.CategoryId is { } id && categoryNames.TryGetValue(id, out var name) ? name : "(uncategorized)",
                Currency: t.CurrencyCode.ToUpperInvariant()))
            .Select(g => new CategoryLine(g.Key.Category, g.Sum(t => t.Amount), g.Key.Currency, g.Count()))
            .OrderByDescending(l => l.Total)];
}

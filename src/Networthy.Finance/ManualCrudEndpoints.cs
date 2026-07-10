using System.Globalization;
using Cortex.Application.Authorization;
using Cortex.Core.Identity;
using Cortex.Core.Multitenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Networthy.Finance.Persistence;

namespace Networthy.Finance;

/// <summary>
/// Manual bookkeeping from the tabs (AI-first ≠ chat-only): the generic tab editors post here.
/// These are the HUMAN acting directly — no AI in the loop means no approval gate; RBAC
/// (<see cref="FinanceModule.ManageFinance"/>) is the gate. Every write keeps the ledger's
/// invariants: balance edits post an adjustment transaction instead of silently rewriting
/// history, and deleting a transaction reverses its balance effect.
/// </summary>
internal static class ManualCrudEndpoints
{
    internal sealed record AccountUpsert(
        string Name, string Type, string CurrencyCode, decimal CachedBalance,
        string? InstitutionName, decimal? InterestRateApr, decimal? MinimumMonthlyPayment);

    internal sealed record TransactionUpsert(
        string AccountName, string Description, decimal Amount, string Direction,
        string? OccurredOn, string? CategoryName);

    internal sealed record BudgetUpsert(string CategoryName, decimal Target, string? CurrencyCode);

    internal sealed record ImportRequest(string FileId, string AccountName);

    internal sealed record IncomeSourceUpsert(
        string Name, decimal Amount, string Cadence, string? CurrencyCode, string? AccountName);

    internal sealed record GoalUpsert(
        string Name, decimal Target, string? CurrencyCode, string? TargetDate, string? AccountName);

    internal static void MapManualCrudEndpoints(this RouteGroupBuilder group)
    {
        var manage = PermissionRequirement.PolicyName(FinanceModule.ManageFinance);

        // ── Accounts ────────────────────────────────────────────────────────────────
        group.MapPost("/accounts", async (
                AccountUpsert body, FinanceDbContext db, ITenantContext tenant, ICurrentUser user,
                CancellationToken ct) =>
            {
                var name = body.Name.Trim();
                if (name.Length == 0)
                {
                    return Results.BadRequest(new { error = "An account needs a name." });
                }

                var type = Account.NormalizeType(body.Type);
                if (type is null)
                {
                    return Results.BadRequest(new { error = $"'{body.Type}' is not an account type. Use checking, savings, credit, cash, or loan." });
                }

                var currency = body.CurrencyCode.Trim().ToUpperInvariant();
                if (currency.Length != 3)
                {
                    return Results.BadRequest(new { error = $"'{body.CurrencyCode}' is not an ISO currency code." });
                }

                if (body.InterestRateApr is < 0 or > 100)
                {
                    return Results.BadRequest(new { error = "APR must be a percentage between 0 and 100." });
                }

                var existing = await db.Accounts.FirstOrDefaultAsync(
                    a => EF.Functions.ILike(a.Name, name), ct);
                if (existing is null)
                {
                    var balance = type is "loan" && body.CachedBalance > 0 ? -body.CachedBalance : body.CachedBalance;
                    db.Accounts.Add(new Account
                    {
                        TenantId = tenant.RequireTenantId(),
                        Name = name,
                        Type = type,
                        CurrencyCode = currency,
                        CachedBalance = balance,
                        InstitutionName = string.IsNullOrWhiteSpace(body.InstitutionName) ? null : body.InstitutionName.Trim(),
                        InterestRateApr = body.InterestRateApr,
                        MinimumMonthlyPayment = body.MinimumMonthlyPayment,
                        CreatedByUserId = user.UserId,
                    });
                    await db.SaveChangesAsync(ct);
                    return Results.Ok(new { name, created = true });
                }

                if (!existing.IsVisibleTo(user.UserId))
                {
                    return Results.NotFound();
                }

                if (existing.Type != type || !existing.CurrencyCode.Equals(currency, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.BadRequest(new { error = "Type and currency are fixed after creation — the ledger's history is in that currency. Create a new account instead." });
                }

                existing.InstitutionName = string.IsNullOrWhiteSpace(body.InstitutionName) ? null : body.InstitutionName.Trim();
                existing.InterestRateApr = body.InterestRateApr;
                existing.MinimumMonthlyPayment = body.MinimumMonthlyPayment;

                // A changed balance is a fact that needs a record — post an adjustment, never rewrite.
                var diff = body.CachedBalance - existing.CachedBalance;
                if (diff != 0)
                {
                    var adjustment = new Transaction
                    {
                        TenantId = existing.TenantId,
                        AccountId = existing.Id,
                        OccurredOn = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime),
                        Amount = Math.Abs(diff),
                        CurrencyCode = existing.CurrencyCode,
                        Description = "Balance adjustment (manual edit)",
                        Direction = diff > 0 ? "income" : "expense",
                        Source = "manual",
                        CreatedByUserId = user.UserId,
                    };
                    db.Transactions.Add(adjustment);
                    existing.CachedBalance += adjustment.BalanceDelta;
                }

                await db.SaveChangesAsync(ct);
                return Results.Ok(new { name, adjusted = diff != 0 });
            })
            .RequireAuthorization(manage)
            .WithName("Finance_UpsertAccount");

        group.MapDelete("/accounts/{id:guid}", async (
                Guid id, FinanceDbContext db, ICurrentUser user, CancellationToken ct) =>
            {
                var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == id, ct);
                if (account is null || !account.IsVisibleTo(user.UserId))
                {
                    return Results.NotFound();
                }

                db.Accounts.Remove(account); // transactions/batches cascade; goals unlink (SetNull)
                await db.SaveChangesAsync(ct);
                return Results.NoContent();
            })
            .RequireAuthorization(manage)
            .WithName("Finance_DeleteAccount");

        // ── Transactions ────────────────────────────────────────────────────────────
        group.MapPost("/transactions", async (
                TransactionUpsert body, FinanceDbContext db, ITenantContext tenant, ICurrentUser user,
                CancellationToken ct) =>
            {
                var account = await db.Accounts.FirstOrDefaultAsync(
                    a => EF.Functions.ILike(a.Name, body.AccountName.Trim()), ct);
                if (account is null || !account.IsVisibleTo(user.UserId))
                {
                    return Results.BadRequest(new { error = $"No account named '{body.AccountName}' exists." });
                }

                if (body.Amount <= 0)
                {
                    return Results.BadRequest(new { error = "Amount must be positive; direction says income vs expense." });
                }

                var direction = Transaction.NormalizeDirection(body.Direction);
                if (direction is null)
                {
                    return Results.BadRequest(new { error = $"'{body.Direction}' is not a direction. Use expense or income." });
                }

                var occurredOn = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
                if (!string.IsNullOrWhiteSpace(body.OccurredOn) &&
                    !DateOnly.TryParse(body.OccurredOn, CultureInfo.InvariantCulture, out occurredOn))
                {
                    return Results.BadRequest(new { error = $"'{body.OccurredOn}' is not a date — use yyyy-MM-dd." });
                }

                Guid? categoryId = null;
                if (!string.IsNullOrWhiteSpace(body.CategoryName))
                {
                    var category = await db.Categories.FirstOrDefaultAsync(
                        c => EF.Functions.ILike(c.Name, body.CategoryName.Trim()), ct);
                    if (category is null)
                    {
                        return Results.BadRequest(new { error = $"No category named '{body.CategoryName}' exists." });
                    }

                    categoryId = category.Id;
                }

                var transaction = new Transaction
                {
                    TenantId = tenant.RequireTenantId(),
                    AccountId = account.Id,
                    OccurredOn = occurredOn,
                    Amount = body.Amount,
                    CurrencyCode = account.CurrencyCode,
                    Description = body.Description.Trim(),
                    CategoryId = categoryId,
                    Direction = direction,
                    Source = "manual",
                    CreatedByUserId = user.UserId,
                };
                db.Transactions.Add(transaction);
                account.CachedBalance += transaction.BalanceDelta;
                await db.SaveChangesAsync(ct);
                return Results.Ok(new { id = transaction.Id });
            })
            .RequireAuthorization(manage)
            .WithName("Finance_AddTransaction");

        group.MapDelete("/transactions/{id:guid}", async (
                Guid id, FinanceDbContext db, ICurrentUser user, CancellationToken ct) =>
            {
                var transaction = await db.Transactions.FirstOrDefaultAsync(t => t.Id == id, ct);
                if (transaction is null)
                {
                    return Results.NotFound();
                }

                var account = await db.Accounts.FirstAsync(a => a.Id == transaction.AccountId, ct);
                if (!account.IsVisibleTo(user.UserId))
                {
                    return Results.NotFound();
                }

                account.CachedBalance -= transaction.BalanceDelta; // the delete reverses the ledger effect
                db.Transactions.Remove(transaction);
                await db.SaveChangesAsync(ct);
                return Results.NoContent();
            })
            .RequireAuthorization(manage)
            .WithName("Finance_DeleteTransaction");

        // ── Statement imports (the setup wizard's upload step posts here) ───────────
        group.MapPost("/imports", async (
                ImportRequest body, StatementImportTools imports, CancellationToken ct) =>
            {
                var message = await imports.ImportStatement(body.FileId, body.AccountName, ct);
                return Results.Ok(new { message });
            })
            .RequireAuthorization(manage)
            .WithName("Finance_QueueImport");

        // ── Budgets (current month) ─────────────────────────────────────────────────
        group.MapPost("/budgets", async (
                BudgetUpsert body, FinanceDbContext db, ITenantContext tenant, CancellationToken ct) =>
            {
                if (body.Target <= 0)
                {
                    return Results.BadRequest(new { error = "Target must be positive." });
                }

                var category = await db.Categories.FirstOrDefaultAsync(
                    c => EF.Functions.ILike(c.Name, body.CategoryName.Trim()), ct);
                if (category is null)
                {
                    return Results.BadRequest(new { error = $"No category named '{body.CategoryName}' exists." });
                }

                BudgetMath.TryParseMonth(null, out var period); // the tab manages the current month
                var currency = (string.IsNullOrWhiteSpace(body.CurrencyCode) ? "USD" : body.CurrencyCode.Trim()).ToUpperInvariant();
                var existing = await db.Budgets.FirstOrDefaultAsync(
                    b => b.CategoryId == category.Id && b.PeriodMonth == period && b.CurrencyCode == currency, ct);
                if (existing is null)
                {
                    db.Budgets.Add(new Budget
                    {
                        TenantId = tenant.RequireTenantId(),
                        CategoryId = category.Id,
                        PeriodMonth = period,
                        TargetAmount = body.Target,
                        CurrencyCode = currency,
                    });
                }
                else
                {
                    existing.TargetAmount = body.Target;
                }

                await db.SaveChangesAsync(ct);
                return Results.Ok(new { category = category.Name, target = body.Target });
            })
            .RequireAuthorization(manage)
            .WithName("Finance_UpsertBudget");

        group.MapDelete("/budgets/{id:guid}", async (Guid id, FinanceDbContext db, CancellationToken ct) =>
            {
                var budget = await db.Budgets.FirstOrDefaultAsync(b => b.Id == id, ct);
                if (budget is null)
                {
                    return Results.NotFound();
                }

                db.Budgets.Remove(budget);
                await db.SaveChangesAsync(ct);
                return Results.NoContent();
            })
            .RequireAuthorization(manage)
            .WithName("Finance_DeleteBudget");

        // ── Income sources ──
        group.MapPost("/income-sources", async (
                IncomeSourceUpsert body, IncomeSourceTools tools, CancellationToken ct) =>
            {
                // Same validation/upsert logic as the chat tool; the form is the human directly.
                var message = await tools.SetIncomeSource(
                    body.Name, (double)body.Amount, body.Cadence,
                    string.IsNullOrWhiteSpace(body.CurrencyCode) ? "USD" : body.CurrencyCode,
                    body.AccountName, ct);
                return message.StartsWith("Income ", StringComparison.Ordinal)
                    ? Results.Ok(new { message })
                    : Results.BadRequest(new { error = message });
            })
            .RequireAuthorization(manage)
            .WithName("Finance_UpsertIncomeSource");

        group.MapDelete("/income-sources/{id:guid}", async (Guid id, FinanceDbContext db, CancellationToken ct) =>
            {
                var source = await db.IncomeSources.FirstOrDefaultAsync(i => i.Id == id, ct);
                if (source is null)
                {
                    return Results.NotFound();
                }

                db.IncomeSources.Remove(source);
                await db.SaveChangesAsync(ct);
                return Results.NoContent();
            })
            .RequireAuthorization(manage)
            .WithName("Finance_DeleteIncomeSource");

        // ── Goals ───────────────────────────────────────────────────────────────────
        group.MapPost("/goals", async (
                GoalUpsert body, FinanceDbContext db, ITenantContext tenant, ICurrentUser user,
                CancellationToken ct) =>
            {
                var name = body.Name.Trim();
                if (name.Length == 0 || body.Target <= 0)
                {
                    return Results.BadRequest(new { error = "A goal needs a name and a positive target." });
                }

                DateOnly? deadline = null;
                if (!string.IsNullOrWhiteSpace(body.TargetDate))
                {
                    if (!DateOnly.TryParse(body.TargetDate, CultureInfo.InvariantCulture, out var parsed))
                    {
                        return Results.BadRequest(new { error = $"'{body.TargetDate}' is not a date — use yyyy-MM-dd." });
                    }

                    deadline = parsed;
                }

                Guid? accountId = null;
                if (!string.IsNullOrWhiteSpace(body.AccountName))
                {
                    var account = await db.Accounts.FirstOrDefaultAsync(
                        a => EF.Functions.ILike(a.Name, body.AccountName.Trim()), ct);
                    if (account is null || !account.IsVisibleTo(user.UserId))
                    {
                        return Results.BadRequest(new { error = $"No account named '{body.AccountName}' exists." });
                    }

                    accountId = account.Id;
                }

                var currency = (string.IsNullOrWhiteSpace(body.CurrencyCode) ? "USD" : body.CurrencyCode.Trim()).ToUpperInvariant();
                var existing = await db.Goals.FirstOrDefaultAsync(g => EF.Functions.ILike(g.Name, name), ct);
                if (existing is null)
                {
                    db.Goals.Add(new Goal
                    {
                        TenantId = tenant.RequireTenantId(),
                        Name = name,
                        TargetAmount = body.Target,
                        CurrencyCode = currency,
                        TargetDate = deadline,
                        AccountId = accountId,
                        CreatedByUserId = user.UserId,
                    });
                }
                else
                {
                    existing.TargetAmount = body.Target;
                    existing.CurrencyCode = currency;
                    existing.TargetDate = deadline;
                    existing.AccountId = accountId;
                }

                await db.SaveChangesAsync(ct);
                return Results.Ok(new { name });
            })
            .RequireAuthorization(manage)
            .WithName("Finance_UpsertGoal");

        group.MapDelete("/goals/{id:guid}", async (Guid id, FinanceDbContext db, CancellationToken ct) =>
            {
                var goal = await db.Goals.FirstOrDefaultAsync(g => g.Id == id, ct);
                if (goal is null)
                {
                    return Results.NotFound();
                }

                db.Goals.Remove(goal);
                await db.SaveChangesAsync(ct);
                return Results.NoContent();
            })
            .RequireAuthorization(manage)
            .WithName("Finance_DeleteGoal");
    }
}

using Cortex.Application.Authorization;
using Cortex.Modules.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Networthy.Finance.Persistence;

namespace Networthy.Finance;

/// <summary>
/// The household-finance vertical (see SPEC.md / ARCH.md): accounts and transactions as the
/// core domain, budgets on top, statements imported with human review, and a chat-first
/// assistant as the primary interface. A household is a Cortex tenant; the platform's RBAC,
/// approval gate, audit log, jobs, and channels apply with no platform changes (ADR-0001).
/// Every record-changing tool is approval-gated except <c>log_own_transaction</c> (ADR-0005).
/// </summary>
public sealed class FinanceModule : IModule
{
    public const string Id = "finance";

    /// <summary>Read access to the household's finance tabs (accounts, transactions, budgets, categories).</summary>
    public const string ViewFinance = "finance.view";

    /// <summary>Curate the household's category taxonomy from the Categories tab.</summary>
    public const string ManageCategories = "finance.categories.manage";

    public ModuleManifest Manifest { get; } = new()
    {
        Id = Id,
        DisplayName = "Finance",
        Version = "0.1.0",
        Description = "Household finances: accounts, transactions, budgets, statement import, and net worth — chat-first, with every AI action approval-gated and audited.",
        Icon = "wallet",
        AgentInstructions =
            "You are Networthy, a household finance assistant. You help a household track accounts, " +
            "transactions, budgets, and net worth. NEVER fabricate a balance, transaction, or budget " +
            "figure — every numeric answer must come from a tool call. You are not a financial advisor: " +
            "you report and organize the household's own data; you do not recommend investments. " +
            "When the user mentions spending or income, offer to record it. Amounts are in the " +
            "account's currency; never guess a currency. " +
            "PLAYBOOK: accounts with create_account/list_accounts; net worth with get_net_worth. " +
            "A member's own purchase -> log_own_transaction (instant, no approval); anything else that " +
            "changes records (categorize_transaction, edit_transaction, create_account) waits for the " +
            "user's approval - tell them so. 'How much did we spend on X' -> summarize_spending. " +
            "'Can I afford X' -> can_i_afford and give the verdict verbatim - never soften a 'no'. " +
            "Suggest a category when logging (match the Categories tab); if none fits, log uncategorized " +
            "and offer categorize_transaction afterwards.",
        Tools =
        [
            new ToolDescriptor
            {
                Name = "create_account",
                Description = "Create a financial account (checking, savings, credit, or cash). Side-effecting: writes data and requires human approval.",
                Permission = Permissions.ForTool(Id, "create_account"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "list_accounts",
                Description = "List the household's accounts with types and current balances (visibility-scoped).",
                Permission = Permissions.ForTool(Id, "list_accounts"),
            },
            new ToolDescriptor
            {
                Name = "get_net_worth",
                Description = "The household's net worth per currency, with the recent trend when snapshots exist.",
                Permission = Permissions.ForTool(Id, "get_net_worth"),
            },
            new ToolDescriptor
            {
                Name = "log_own_transaction",
                Description = "Log the caller's own transaction. Quick capture - the module's ONE ungated write (ADR-0005); correctable with edit_transaction.",
                Permission = Permissions.ForTool(Id, "log_own_transaction"),
            },
            new ToolDescriptor
            {
                Name = "categorize_transaction",
                Description = "Set or change a transaction's category (AI suggestions land through this). Side-effecting: writes data and requires human approval.",
                Permission = Permissions.ForTool(Id, "categorize_transaction"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "edit_transaction",
                Description = "Correct a transaction's amount, description, or date; balances adjust. Side-effecting: writes data and requires human approval.",
                Permission = Permissions.ForTool(Id, "edit_transaction"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "search_transactions",
                Description = "Search transactions by text, category, and/or date range.",
                Permission = Permissions.ForTool(Id, "search_transactions"),
            },
            new ToolDescriptor
            {
                Name = "summarize_spending",
                Description = "Spending or income summed by category over a period - 'how much did we spend on X'.",
                Permission = Permissions.ForTool(Id, "summarize_spending"),
            },
            new ToolDescriptor
            {
                Name = "can_i_afford",
                Description = "Direct 'can I afford X?' verdict from liquid balances and this month's spending. Read-only.",
                Permission = Permissions.ForTool(Id, "can_i_afford"),
            },
        ],
        Tabs =
        [
            new TabDescriptor { Id = "chat", Label = "Chat", Route = "/finance/chat", Icon = "message-circle", Order = 0 },
            new TabDescriptor
            {
                Id = "accounts", Label = "Accounts", Route = "/finance/accounts", Icon = "landmark", Order = 1,
                Permission = ViewFinance,
                DataEndpoint = "/api/finance/accounts",
                Columns =
                [
                    new("name", "Account"), new("type", "Type"), new("institutionName", "Institution"),
                    new("cachedBalance", "Balance"), new("currencyCode", "Currency"),
                ],
                Placeholder = "No accounts yet. Accounts are created in Chat - try: 'Create a checking account called Chase Checking in USD with balance 2500'. The assistant asks for your approval before anything is created.",
            },
            new TabDescriptor
            {
                Id = "transactions", Label = "Transactions", Route = "/finance/transactions", Icon = "receipt", Order = 2,
                Permission = ViewFinance,
                DataEndpoint = "/api/finance/transactions",
                Columns =
                [
                    new("occurredOn", "Date"), new("description", "Description"), new("amount", "Amount"),
                    new("currencyCode", "Currency"), new("direction", "Direction"),
                    new("categoryName", "Category"), new("accountName", "Account"),
                ],
                Placeholder = "No transactions yet. Capture them in Chat - try: 'Log $6.50 coffee on Chase Checking' - or upload a bank statement and review the extracted lines.",
            },
            new TabDescriptor
            {
                Id = "categories", Label = "Categories", Route = "/finance/categories", Icon = "tags", Order = 5,
                Permission = ViewFinance,
                DataEndpoint = "/api/finance/categories",
                Columns = [new("name", "Category"), new("parentName", "Parent")],
                Placeholder = "No categories yet — the starter taxonomy seeds on first run; curate it here.",
                // Household admins curate the taxonomy right in the table (permission-gated in the
                // payload and on the endpoints); members see it read-only.
                Editor = new TabEditor
                {
                    UpsertEndpoint = "/api/finance/categories",
                    DeleteEndpoint = "/api/finance/categories/{id}",
                    Permission = ManageCategories,
                    KeyField = "id",
                    Fields =
                    [
                        new("name", "Category name"),
                        new("parentName", "Parent category (optional, must exist)"),
                    ],
                },
            },
        ],
    };

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<FinanceDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString(FinanceDbContext.ConnectionName)));
        services.AddScoped<AccountTools>();
        services.AddScoped<TransactionTools>();
        services.AddScoped<AffordabilityTools>();
        services.AddSingleton<IModuleToolSource, FinanceToolSource>();
        services.AddHostedService<NetWorthSnapshotService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/finance").WithTags("Finance").RequireAuthorization();

        group.MapGet("/accounts", async (
                FinanceDbContext db, Cortex.Core.Identity.ICurrentUser currentUser,
                CancellationToken cancellationToken) =>
            {
                var accounts = (await db.Accounts.OrderBy(a => a.Name).Take(200).ToListAsync(cancellationToken))
                    .Where(a => a.IsVisibleTo(currentUser.UserId));
                return Results.Ok(accounts.Select(a => new
                {
                    id = a.Id, name = a.Name, type = a.Type, institutionName = a.InstitutionName,
                    cachedBalance = a.CachedBalance, currencyCode = a.CurrencyCode,
                }));
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ViewFinance))
            .WithName("Finance_Accounts");

        group.MapGet("/transactions", async (
                FinanceDbContext db, Cortex.Core.Identity.ICurrentUser currentUser,
                CancellationToken cancellationToken) =>
            {
                var visibleAccounts = (await db.Accounts.ToListAsync(cancellationToken))
                    .Where(a => a.IsVisibleTo(currentUser.UserId))
                    .ToDictionary(a => a.Id, a => a.Name);
                var categoryNames = await db.Categories.ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
                var rows = (await db.Transactions.OrderByDescending(t => t.OccurredOn).Take(500).ToListAsync(cancellationToken))
                    .Where(t => visibleAccounts.ContainsKey(t.AccountId))
                    .Take(200);
                return Results.Ok(rows.Select(t => new
                {
                    id = t.Id,
                    occurredOn = t.OccurredOn.ToString("yyyy-MM-dd"),
                    description = t.Description,
                    amount = t.Amount,
                    currencyCode = t.CurrencyCode,
                    direction = t.Direction,
                    categoryName = t.CategoryId is { } c && categoryNames.TryGetValue(c, out var name) ? name : null,
                    accountName = visibleAccounts[t.AccountId],
                }));
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ViewFinance))
            .WithName("Finance_Transactions");

        group.MapGet("/categories", async (FinanceDbContext db, CancellationToken cancellationToken) =>
            {
                var categories = await db.Categories.OrderBy(c => c.Name).Take(500).ToListAsync(cancellationToken);
                var names = categories.ToDictionary(c => c.Id, c => c.Name);
                return Results.Ok(categories.Select(c => new CategoryDto(
                    c.Id, c.Name,
                    c.ParentCategoryId is { } p && names.TryGetValue(p, out var parent) ? parent : null)));
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ViewFinance))
            .WithName("Finance_Categories");

        group.MapPost("/categories", async (
                UpsertCategoryRequest body, FinanceDbContext db,
                Cortex.Core.Multitenancy.ITenantContext tenant, CancellationToken cancellationToken) =>
            {
                var name = body.Name.Trim();
                if (name.Length == 0)
                {
                    return Results.BadRequest(new { error = "A category needs a name." });
                }

                Guid? parentId = null;
                if (!string.IsNullOrWhiteSpace(body.ParentName))
                {
                    var parent = await db.Categories.FirstOrDefaultAsync(
                        c => EF.Functions.ILike(c.Name, body.ParentName.Trim()), cancellationToken);
                    if (parent is null)
                    {
                        return Results.BadRequest(new { error = $"No parent category named '{body.ParentName}' exists." });
                    }

                    parentId = parent.Id;
                }

                var existing = body.Id is { } id
                    ? await db.Categories.FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
                    : await db.Categories.FirstOrDefaultAsync(c => EF.Functions.ILike(c.Name, name), cancellationToken);
                if (existing is null)
                {
                    existing = new Category { TenantId = tenant.RequireTenantId(), Name = name, ParentCategoryId = parentId };
                    db.Categories.Add(existing);
                }
                else
                {
                    existing.Name = name;
                    existing.ParentCategoryId = parentId;
                }

                await db.SaveChangesAsync(cancellationToken);
                return Results.Ok(new CategoryDto(existing.Id, existing.Name, body.ParentName));
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ManageCategories))
            .WithName("Finance_UpsertCategory");

        group.MapDelete("/categories/{id:guid}", async (
                Guid id, FinanceDbContext db, CancellationToken cancellationToken) =>
            {
                var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
                if (category is null)
                {
                    return Results.NotFound();
                }

                var hasChildren = await db.Categories.AnyAsync(c => c.ParentCategoryId == id, cancellationToken);
                if (hasChildren)
                {
                    return Results.BadRequest(new { error = "This category has subcategories — remove or re-parent them first." });
                }

                db.Categories.Remove(category);
                await db.SaveChangesAsync(cancellationToken);
                return Results.NoContent();
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ManageCategories))
            .WithName("Finance_DeleteCategory");
    }

    public async Task MigrateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var db = services.GetRequiredService<FinanceDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
    }

    public async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var tenant = services.GetRequiredService<Cortex.Core.Multitenancy.ITenantContext>();
        if (!tenant.HasTenant)
        {
            return;
        }

        var db = services.GetRequiredService<FinanceDbContext>();
        if (await db.Categories.AnyAsync(cancellationToken))
        {
            return;
        }

        var tenantId = tenant.RequireTenantId();
        foreach (var name in FinanceCatalog.StarterCategories)
        {
            db.Categories.Add(new Category { TenantId = tenantId, Name = name });
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed record CategoryDto(Guid Id, string Name, string? ParentName);

public sealed record UpsertCategoryRequest(Guid? Id, string Name, string? ParentName);

/// <summary>Seed data every new household starts from (curated from there — never re-imposed).</summary>
public static class FinanceCatalog
{
    /// <summary>The starter category taxonomy, deliberately flat — households add subcategories as needed.</summary>
    public static readonly IReadOnlyList<string> StarterCategories =
    [
        "Housing", "Utilities", "Groceries", "Dining", "Transportation", "Health", "Insurance",
        "Entertainment", "Shopping", "Subscriptions", "Travel", "Education", "Personal Care",
        "Debt Payments", "Savings", "Gifts & Donations", "Salary", "Interest", "Other Income", "Other",
    ];
}

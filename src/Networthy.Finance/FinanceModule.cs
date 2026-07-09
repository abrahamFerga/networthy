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
            "account's currency; never guess a currency.",
        Tools = [],
        Tabs =
        [
            new TabDescriptor { Id = "chat", Label = "Chat", Route = "/finance/chat", Icon = "message-circle", Order = 0 },
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
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/finance").WithTags("Finance").RequireAuthorization();

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

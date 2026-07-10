using Cortex.Application.Authorization;
using Cortex.Application.Commerce;
using Cortex.AspNetCore.Connectors;
using Cortex.AspNetCore.Hosting;
using Cortex.AspNetCore.Modules;
using Networthy.Connectors.Plaid;
using Networthy.Finance;

// ─────────────────────────────────────────────────────────────────────────────
// Networthy — a single-vertical household-finance system built ENTIRELY on the
// Cortex base platform (ADR-0001). This host is deliberately thin: install the
// platform, install the finance module, install the product-owned Plaid
// connector (ADR-0007), declare what the product sells and its roles. Domain
// logic lives in Networthy.Finance; platform behavior lives in the packages.
// ─────────────────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

builder.AddCortexPlatform();

builder.AddCortexModule<FinanceModule>();

// The product-owned, domain-specific connector (ADR-0007): defined in THIS repo against
// Cortex.Connectors.Sdk, registered exactly like a built-in. Default-off; a household
// admin enables and configures it under Integrations.
builder.AddCortexConnector<PlaidConnector>();

// What this product sells (the plan — not checkout metadata — decides what a purchase grants).
builder.Services.AddCortexProduct(new ProductOffering
{
    ProductId = "networthy",
    Plans =
    [
        new ProductPlan { Id = "solo", Modules = ["finance"], DefaultSeats = 1, MonthlyTokenBudget = 200_000 },
        new ProductPlan { Id = "household", Modules = ["finance"], DefaultSeats = 6, MonthlyTokenBudget = 500_000 },
        new ProductPlan { Id = "dedicated", Dedicated = true },
    ],
});

// The household roles (SPEC.md's RBAC model). Seeded into every tenant's editable baseline;
// a household admin refines them per tenant afterwards. system_admin stays non-customizable.
builder.Services.AddCortexRole("household-admin",
[
    "chat.use", "chat.conversations.view", "files.upload", "files.read",
    "tools.documents.read_document", "tools.documents.list_documents",
    // Every finance tool, read and write (writes stay approval-gated at the tool layer).
    "tools.finance.*",
    // Plaid connector management + tools.
    "tools.connectors.plaid.*",
    FinanceModule.ViewFinance, FinanceModule.ManageCategories, FinanceModule.ReviewImports,
    FinanceModule.ManageFinance,
]);
builder.Services.AddCortexRole("household-member",
[
    "chat.use", "chat.conversations.view", "files.read",
    // Reads + the one ungated quick-capture write (ADR-0005). No account/category/budget
    // management, no statement import, no Plaid, no approving others' pending actions.
    "tools.finance.list_accounts", "tools.finance.get_net_worth",
    "tools.finance.search_transactions", "tools.finance.summarize_spending",
    "tools.finance.log_own_transaction", "tools.finance.can_i_afford",
    "tools.finance.get_budget_status",
    // Goals: members see progress and may propose contributions (approval-gated like any write).
    "tools.finance.list_goals", "tools.finance.contribute_to_goal",
    // Everyone in the household may ask how the household is doing.
    "tools.finance.get_financial_health",
    FinanceModule.ViewFinance,
]);

var app = builder.Build();

await app.RunCortexPlatformAsync();

/// <summary>Exposed so integration tests can host this app via WebApplicationFactory&lt;Program&gt;.</summary>
public partial class Program;

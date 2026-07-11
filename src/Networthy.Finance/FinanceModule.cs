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

    /// <summary>Work the Statement review tab: correct extracted lines and approve the batch.</summary>
    public const string ReviewImports = "finance.imports.review";

    /// <summary>Manage the books by hand from the tabs: add/edit/delete accounts, goals,
    /// budgets, and transactions without going through chat. The forms are the human acting
    /// directly, so no AI approval gate applies — RBAC is the gate.</summary>
    public const string ManageFinance = "finance.manage";

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
            "and offer categorize_transaction afterwards. STATEMENTS: when the user attaches a bank " +
            "statement, import_statement (file id from the attachment block, plus the account); then " +
            "review_import_batch to show the extracted lines, and only after the user confirms, " +
            "approve_import_batch. Never post lines the user hasn't seen. HOUSEHOLD: members and " +
            "roles are managed by admins under Admin -> Users (household-admin / household-member); " +
            "set_account_visibility makes an account private to its owner or shared again. BUDGETS: " +
            "set_budget for targets ('set the grocery budget to $400'); get_budget_status answers " +
            "'how are we doing this month' and flags OVER categories - report those honestly. " +
            "TRANSPARENCY: list_pending_approvals shows what you are waiting on; get_activity_log shows " +
            "what changed and where it came from - offer these whenever the user asks what the AI did. " +
            "GOALS: set_goal for savings targets ('save $5,000 for Hawaii by June'), optionally tracked " +
            "by an account's balance; contribute_to_goal records progress on unlinked goals (a marker, " +
            "never a transaction); list_goals answers 'am I on track' with the computed pace - report " +
            "'behind pace' honestly. The Net worth tab charts the household's trend; the Statement " +
            "review tab is where extracted lines get corrected and approved outside of chat. " +
            "PREFERENCES: the household sets its default currency, time zone, and reminder lead in the "
            + "Settings tab or via update_household_settings; when the user says amounts without a "
            + "currency, tools use that default - never assume USD for a household configured otherwise. "
            + "SUBSCRIPTIONS: 'what subscriptions do we have' / 'what are we paying for' -> list_recurring; report price rises and upcoming charges honestly, and note detection is conservative (three steady occurrences minimum). "
            + "INCOME & PLANS: 'I get paid X every two weeks' -> set_income_source (cadence matters: " +
            "biweekly is 26 pays/year). 'Save 3,000 for vacations by June' or '100,000 by age 55, " +
            "invested' -> the goal needs a target DATE (convert an age: ask the year it lands on), and " +
            "invested goals need expectedAnnualReturnPct on set_goal - ALWAYS ask the user for their " +
            "assumed return, NEVER pick one yourself, and repeat that it is an assumption, not a promise. " +
            "Then get_goal_plan and relay it verbatim: monthly and per-paycheck amounts, where to save, " +
            "whether it fits their cash flow. " +
            "DEBTS & HEALTH: loans (mortgage/auto/student) are accounts of type loan - create_account " +
            "with interestRateApr and minimumMonthlyPayment; NEVER guess a rate, ask. " +
            "update_account_terms corrects APR/minimum on any debt. 'How is our financial health' or " +
            "'how do we improve' -> get_financial_health and relay its computed numbers and its " +
            "suggestions - do not invent strategies beyond the tool's list, and repeat its framing: " +
            "standard strategies from their own data, not financial advice.",
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
            new ToolDescriptor
            {
                Name = "list_pending_approvals",
                Description = "Everything the AI is waiting on: this household's pending approval requests.",
                Permission = Permissions.ForTool(Id, "list_pending_approvals"),
            },
            new ToolDescriptor
            {
                Name = "get_activity_log",
                Description = "Recent changes to the household's books - what, when, by whom, from which source.",
                Permission = Permissions.ForTool(Id, "get_activity_log"),
            },
            new ToolDescriptor
            {
                Name = "set_budget",
                Description = "Set or change a category's monthly budget target. Side-effecting: writes data and requires human approval.",
                Permission = Permissions.ForTool(Id, "set_budget"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "get_budget_status",
                Description = "Spent vs target per category for a month, with over-budget flags.",
                Permission = Permissions.ForTool(Id, "get_budget_status"),
            },
            new ToolDescriptor
            {
                Name = "set_account_visibility",
                Description = "Make an account private to the caller or shared with the household. Side-effecting: writes data and requires human approval.",
                Permission = Permissions.ForTool(Id, "set_account_visibility"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "import_statement",
                Description = "Import an uploaded bank statement (CSV/OFX/QFX) for extraction and review. Side-effecting: brings external data in and requires human approval.",
                Permission = Permissions.ForTool(Id, "import_statement"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "review_import_batch",
                Description = "Show an import batch's extracted lines for review before approval.",
                Permission = Permissions.ForTool(Id, "review_import_batch"),
            },
            new ToolDescriptor
            {
                Name = "approve_import_batch",
                Description = "Post a reviewed batch's lines as transactions. Side-effecting: writes data and requires human approval.",
                Permission = Permissions.ForTool(Id, "approve_import_batch"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "set_goal",
                Description = "Create or update a savings goal, optionally tracked by an account's balance. Side-effecting: writes data and requires human approval.",
                Permission = Permissions.ForTool(Id, "set_goal"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "contribute_to_goal",
                Description = "Record progress toward an unlinked goal (a bookkeeping marker, not a transaction). Side-effecting: writes data and requires human approval.",
                Permission = Permissions.ForTool(Id, "contribute_to_goal"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "list_goals",
                Description = "Savings goals with computed progress and on-pace verdicts.",
                Permission = Permissions.ForTool(Id, "list_goals"),
            },
            new ToolDescriptor
            {
                Name = "update_account_terms",
                Description = "Set or correct a debt account's interest rate (APR) and minimum payment. Side-effecting: writes data and requires human approval.",
                Permission = Permissions.ForTool(Id, "update_account_terms"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "get_financial_health",
                Description = "Computed financial-health check: net worth, debt cost, emergency fund, savings rate, plus standard data-triggered improvement strategies. Read-only.",
                Permission = Permissions.ForTool(Id, "get_financial_health"),
            },
            new ToolDescriptor
            {
                Name = "set_income_source",
                Description = "Declare or update a recurring income with its cadence (weekly/biweekly/semimonthly/monthly). Side-effecting: writes data and requires human approval.",
                Permission = Permissions.ForTool(Id, "set_income_source"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "list_income_sources",
                Description = "Declared income schedules with monthly equivalents.",
                Permission = Permissions.ForTool(Id, "list_income_sources"),
            },
            new ToolDescriptor
            {
                Name = "get_goal_plan",
                Description = "How to reach a goal: required contribution per month and per paycheck, where to save it, cash-flow fit. Uses the goal's own assumed return for invested goals. Read-only.",
                Permission = Permissions.ForTool(Id, "get_goal_plan"),
            },
            new ToolDescriptor
            {
                Name = "list_recurring",
                Description = "Detected recurring charges (subscriptions, bills) with cadence, monthly cost, price-rise flags, and what is due soon. Read-only.",
                Permission = Permissions.ForTool(Id, "list_recurring"),
            },
            new ToolDescriptor
            {
                Name = "get_household_settings",
                Description = "The household's preferences: default currency, time zone, reminder lead time.",
                Permission = Permissions.ForTool(Id, "get_household_settings"),
            },
            new ToolDescriptor
            {
                Name = "update_household_settings",
                Description = "Change the household's default currency, time zone, reminder lead time, emergency-fund floor, or high-APR threshold. Side-effecting: writes data and requires human approval.",
                Permission = Permissions.ForTool(Id, "update_household_settings"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "set_exchange_rate",
                Description = "Save the household's own conversion rate for a foreign currency so multi-currency net worth combines into the default currency. Side-effecting: writes data and requires human approval.",
                Permission = Permissions.ForTool(Id, "set_exchange_rate"),
                RequiresApproval = true,
            },
        ],
        Onboarding = new OnboardingDescriptor
        {
            ProbeEndpoint = "/api/finance/accounts",
            Permission = ManageFinance,
            Title = "Set up your household finances",
            Steps =
            [
                new OnboardingStep
                {
                    Id = "welcome", Kind = "info", Title = "Welcome to Networthy",
                    Blurb = "A few guided minutes: your accounts, your income, your past expenses, your loans. " +
                            "Everything here is skippable and everything can be done later — from the tabs or by " +
                            "asking the assistant in Chat (it always asks your approval before changing anything).",
                },
                new OnboardingStep
                {
                    Id = "basics", Kind = "form", Title = "Your household basics",
                    Blurb = "The currency you think in and the time zone you live in — every amount, " +
                            "every 'today', every reminder uses these. One entry, set once.",
                    Endpoint = "/api/finance/settings",
                    Fields =
                    [
                        new("defaultCurrencyCode", "Default currency (ISO, e.g. MXN or USD)"),
                        new("timeZoneId", "Time zone (IANA id, e.g. America/Mexico_City; empty = UTC)", Required: false),
                    ],
                },
                new OnboardingStep
                {
                    Id = "accounts", Kind = "form", Title = "Where does your money live?",
                    Blurb = "Add your everyday accounts — checking, savings, credit cards, cash. Balances can be " +
                            "estimates; statement imports and adjustments true them up later.",
                    Endpoint = "/api/finance/accounts",
                    Fields =
                    [
                        new("name", "Account name"),
                        new("type", "Type", Options: ["checking", "savings", "credit", "cash"]),
                        new("currencyCode", "Currency (ISO, e.g. USD)"),
                        new("cachedBalance", "Current balance (negative = owed)", Numeric: true),
                        new("institutionName", "Institution", Required: false),
                    ],
                },
                new OnboardingStep
                {
                    Id = "income", Kind = "form", Title = "Your regular income",
                    Blurb = "Record your most recent paycheck (or other income) so spending summaries, savings " +
                            "rate, and the financial-health check have something to measure against.",
                    Endpoint = "/api/finance/transactions",
                    Preset = new Dictionary<string, string> { ["direction"] = "income", ["categoryName"] = "Salary" },
                    Fields =
                    [
                        new("accountName", "Which account does it land in?", OptionsEndpoint: "/api/finance/accounts", OptionsField: "name"),
                        new("amount", "Amount", Numeric: true),
                        new("description", "Description (e.g. 'ACME payroll')"),
                        new("occurredOn", "Date (yyyy-MM-dd, optional = today)", Required: false),
                    ],
                },
                new OnboardingStep
                {
                    Id = "statements", Kind = "upload", Title = "Your past expenses",
                    Blurb = "Attach recent bank statements (CSV, OFX/QFX, or PDF). Networthy extracts every line " +
                            "in the background and NOTHING posts until you review and approve it — the Statement " +
                            "review tab is where that happens.",
                    Endpoint = "/api/finance/imports",
                    FileIdField = "fileId",
                    Accept = ".csv,.ofx,.qfx,.pdf",
                    Fields = [new("accountName", "Which account are these statements from?", OptionsEndpoint: "/api/finance/accounts", OptionsField: "name")],
                },
                new OnboardingStep
                {
                    Id = "loans", Kind = "form", Title = "Loans and debts",
                    Blurb = "Mortgage, car, student, personal — with the interest rate, Networthy can tell you " +
                            "what each debt actually costs per month and which to pay down first.",
                    Endpoint = "/api/finance/accounts",
                    Preset = new Dictionary<string, string> { ["type"] = "loan" },
                    Fields =
                    [
                        new("name", "Loan name (e.g. 'House mortgage')"),
                        new("currencyCode", "Currency (ISO, e.g. USD)"),
                        new("cachedBalance", "Amount owed", Numeric: true),
                        new("interestRateApr", "Interest rate (APR %)", Required: false, Numeric: true),
                        new("minimumMonthlyPayment", "Minimum monthly payment", Required: false, Numeric: true),
                        new("institutionName", "Lender", Required: false),
                    ],
                },
                new OnboardingStep
                {
                    Id = "budget", Kind = "form", Title = "A first budget",
                    Blurb = "Pick one category you care about (Groceries and Dining are popular first picks) and " +
                            "give it a monthly target. Over-budget flags show up in Chat and on the Budgets tab.",
                    Endpoint = "/api/finance/budgets",
                    Fields =
                    [
                        new("categoryName", "Category", OptionsEndpoint: "/api/finance/categories", OptionsField: "name"),
                        new("target", "Monthly target", Numeric: true),
                    ],
                },
                new OnboardingStep
                {
                    Id = "done", Kind = "info", Title = "What to try first",
                    Blurb = "Ask the assistant: 'How is our financial health?' for a computed check of net worth, " +
                            "debt cost, and savings rate. If you uploaded statements, visit Statement review to " +
                            "approve the extracted lines. And the Net worth tab starts charting from today.",
                },
            ],
        },

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
                Placeholder = "No accounts yet. Add one right here, or ask in Chat - 'Create a checking account called Chase Checking in USD with balance 2500' (the assistant asks for approval first).",
                Editor = new TabEditor
                {
                    UpsertEndpoint = "/api/finance/accounts",
                    DeleteEndpoint = "/api/finance/accounts/{id}",
                    Permission = ManageFinance,
                    KeyField = "name",
                    Fields =
                    [
                        new("name", "Account name"),
                        new("type", "Type", Options: ["checking", "savings", "credit", "cash", "loan"]),
                        new("currencyCode", "Currency (ISO, e.g. USD)"),
                        new("cachedBalance", "Balance (negative = owed; edits post an adjustment)", Numeric: true),
                        new("institutionName", "Institution (optional)", Required: false),
                        new("interestRateApr", "APR % (credit/loan, optional)", Required: false, Numeric: true),
                        new("minimumMonthlyPayment", "Minimum payment (optional)", Required: false, Numeric: true),
                    ],
                },
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
                Placeholder = "No transactions yet. Add one here, capture it in Chat ('Log $6.50 coffee on Chase Checking'), or upload a bank statement and review the extracted lines.",
                Editor = new TabEditor
                {
                    UpsertEndpoint = "/api/finance/transactions",
                    DeleteEndpoint = "/api/finance/transactions/{id}",
                    Permission = ManageFinance,
                    // No key field: transactions are append-and-delete; corrections go through
                    // delete+re-add here or edit_transaction in chat.
                    Fields =
                    [
                        new("accountName", "Account", OptionsEndpoint: "/api/finance/accounts", OptionsField: "name"),
                        new("description", "Description"),
                        new("amount", "Amount (positive)", Numeric: true),
                        new("direction", "Direction", Options: ["expense", "income"]),
                        new("occurredOn", "Date (yyyy-MM-dd, optional = today)", Required: false),
                        new("categoryName", "Category (optional)", Required: false, OptionsEndpoint: "/api/finance/categories", OptionsField: "name"),
                    ],
                },
            },
            new TabDescriptor
            {
                Id = "budgets", Label = "Budgets", Route = "/finance/budgets", Icon = "target", Order = 3,
                Permission = ViewFinance,
                DataEndpoint = "/api/finance/budgets",
                Columns =
                [
                    new("categoryName", "Category"), new("spent", "Spent"), new("target", "Target"),
                    new("remaining", "Remaining"), new("currencyCode", "Currency"), new("status", "Status"),
                ],
                Placeholder = "No budgets for this month. Set them here or in Chat ('Set the grocery budget to $400'). Last month's targets roll forward automatically once you have some.",
                Editor = new TabEditor
                {
                    UpsertEndpoint = "/api/finance/budgets",
                    DeleteEndpoint = "/api/finance/budgets/{id}",
                    Permission = ManageFinance,
                    KeyField = "categoryName",
                    Fields =
                    [
                        new("categoryName", "Category", OptionsEndpoint: "/api/finance/categories", OptionsField: "name"),
                        new("target", "Monthly target", Numeric: true),
                        new("currencyCode", "Currency (default USD)", Required: false),
                    ],
                },
            },
            new TabDescriptor
            {
                Id = "income", Label = "Income", Route = "/finance/income", Icon = "banknote", Order = 4,
                Permission = ViewFinance,
                DataEndpoint = "/api/finance/income-sources",
                Columns =
                [
                    new("name", "Income"), new("amount", "Per paycheck"), new("cadence", "Cadence"),
                    new("monthlyEquivalent", "\u2248 Monthly"), new("currencyCode", "Currency"),
                ],
                Placeholder = "No income schedules yet. Add one here or in Chat - 'I get paid 2,500 every two weeks from ACME'. Schedules power per-paycheck goal plans and cash-flow checks.",
                Editor = new TabEditor
                {
                    UpsertEndpoint = "/api/finance/income-sources",
                    DeleteEndpoint = "/api/finance/income-sources/{id}",
                    Permission = ManageFinance,
                    KeyField = "name",
                    Fields =
                    [
                        new("name", "Income name (e.g. 'ACME payroll')"),
                        new("amount", "Amount per paycheck", Numeric: true),
                        new("cadence", "Cadence", Options: ["weekly", "biweekly", "semimonthly", "monthly"]),
                        new("currencyCode", "Currency (default USD)", Required: false),
                        new("accountName", "Lands in account (optional)", Required: false, OptionsEndpoint: "/api/finance/accounts", OptionsField: "name"),
                    ],
                },
            },
            new TabDescriptor
            {
                Id = "recurring", Label = "Recurring", Route = "/finance/recurring", Icon = "repeat", Order = 5,
                Permission = ViewFinance,
                DataEndpoint = "/api/finance/recurring",
                Columns =
                [
                    new("name", "Charge"), new("cadence", "Cadence"), new("average", "Average"),
                    new("monthlyCost", "Monthly cost"), new("nextExpected", "Next expected"),
                    new("status", "Status"),
                ],
                Placeholder = "No recurring charges detected yet. Detection needs at least three same-merchant charges at a steady rhythm - import a few months of statements and check back.",
            },
            new TabDescriptor
            {
                Id = "debts", Label = "Debts", Route = "/finance/debts", Icon = "credit-card", Order = 6,
                Permission = ViewFinance,
                DataEndpoint = "/api/finance/debts",
                Columns =
                [
                    new("name", "Debt"), new("type", "Type"), new("owed", "Owed"),
                    new("apr", "APR %"), new("monthlyInterest", "Interest/mo"),
                    new("minimumPayment", "Min payment"), new("currencyCode", "Currency"),
                ],
                Placeholder = "No debts recorded. Add them in Chat - try: 'Add my mortgage: 250,000 at 6.25% with Wells Fargo' - and ask 'how is our financial health?' once they're in.",
            },
            new TabDescriptor
            {
                Id = "trend", Label = "Net worth", Route = "/finance/trend", Icon = "trending-up", Order = 7,
                Permission = ViewFinance,
                DataEndpoint = "/api/finance/net-worth/history",
                // The platform renders these rows as a time-series line chart — one line per currency.
                Chart = new TabChart { XField = "takenOn", YField = "netWorth", SeriesField = "currencyCode", YLabel = "Net worth" },
                Placeholder = "The trend appears once daily snapshots accumulate — check back tomorrow.",
            },
            new TabDescriptor
            {
                Id = "goals", Label = "Goals", Route = "/finance/goals", Icon = "flag", Order = 8,
                Permission = ViewFinance,
                DataEndpoint = "/api/finance/goals",
                Columns =
                [
                    new("name", "Goal"), new("saved", "Saved"), new("target", "Target"),
                    new("progress", "Progress"), new("currencyCode", "Currency"),
                    new("targetDate", "Target date"), new("pace", "Pace"),
                ],
                Placeholder = "No goals yet. Add one here, or in Chat - 'Save $5,000 for Hawaii by June' (the assistant asks for approval first).",
                Editor = new TabEditor
                {
                    UpsertEndpoint = "/api/finance/goals",
                    DeleteEndpoint = "/api/finance/goals/{id}",
                    Permission = ManageFinance,
                    KeyField = "name",
                    Fields =
                    [
                        new("name", "Goal name"),
                        new("target", "Target amount", Numeric: true),
                        new("currencyCode", "Currency (default USD)", Required: false),
                        new("targetDate", "Target date (yyyy-MM-dd, optional)", Required: false),
                        new("accountName", "Tracked account (optional - its balance becomes the progress)", Required: false, OptionsEndpoint: "/api/finance/accounts", OptionsField: "name"),
                    ],
                },
            },
            new TabDescriptor
            {
                Id = "review", Label = "Statement review", Route = "/finance/review", Icon = "list-checks", Order = 9,
                Permission = ViewFinance,
                DataEndpoint = "/api/finance/imports/latest/lines",
                Columns =
                [
                    new("index", "#"), new("date", "Date"), new("description", "Description"),
                    new("amount", "Amount"), new("direction", "Direction"), new("category", "Category"),
                ],
                Placeholder = "Nothing awaiting review. Attach a bank statement in Chat and ask to import it; the extracted lines land here for correction and approval.",
                // Reviewers fix a line's category (or drop a bogus line) right in the table…
                Editor = new TabEditor
                {
                    UpsertEndpoint = "/api/finance/imports/latest/lines",
                    DeleteEndpoint = "/api/finance/imports/latest/lines/{index}",
                    Permission = ReviewImports,
                    KeyField = "index",
                    Fields =
                    [
                        new("index", "Line # (from the table)", Numeric: true),
                        new("category", "Category", Required: false, OptionsEndpoint: "/api/finance/categories", OptionsField: "name"),
                    ],
                },
                // …and one button posts the whole batch. The human clicking IS the approval.
                Actions =
                [
                    new TabAction
                    {
                        Id = "approve-latest",
                        Label = "Approve batch",
                        Endpoint = "/api/finance/imports/latest/approve",
                        Permission = ReviewImports,
                        Confirm = "Post every line above as transactions? Balances update immediately.",
                    },
                ],
            },
            new TabDescriptor
            {
                Id = "settings", Label = "Settings", Route = "/finance/settings", Icon = "settings", Order = 11,
                Permission = ViewFinance,
                DataEndpoint = "/api/finance/settings",
                Columns =
                [
                    new("defaultCurrencyCode", "Default currency"), new("timeZoneId", "Time zone"),
                    new("todayThere", "Today there"), new("billReminderLeadDays", "Reminder lead (days)"),
                    new("emergencyFundFloorMonths", "Emergency floor (months)"),
                    new("highAprThresholdPercent", "High-APR threshold (%)"),
                ],
                Placeholder = "Defaults apply: USD, UTC, reminders 3 days ahead. Set your household's own here.",
                Editor = new TabEditor
                {
                    UpsertEndpoint = "/api/finance/settings",
                    Permission = ManageFinance,
                    Fields =
                    [
                        new("defaultCurrencyCode", "Default currency (ISO, e.g. MXN)"),
                        new("timeZoneId", "Time zone (IANA id, e.g. America/Mexico_City; empty = UTC)", Required: false),
                        new("billReminderLeadDays", "Bill reminder lead (days, 0-14)", Required: false, Numeric: true),
                        new("emergencyFundFloorMonths", "Emergency-fund floor (months, 0-24)", Required: false, Numeric: true),
                        new("highAprThresholdPercent", "High-APR threshold (%, 0-100)", Required: false, Numeric: true),
                    ],
                },
            },
            new TabDescriptor
            {
                Id = "categories", Label = "Categories", Route = "/finance/categories", Icon = "tags", Order = 10,
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
        services.AddScoped<StatementImportTools>();
        services.AddScoped<HouseholdTools>();
        services.AddScoped<BudgetTools>();
        services.AddScoped<ApprovalSurfaceTools>();
        services.AddScoped<GoalTools>();
        services.AddScoped<HealthTools>();
        services.AddScoped<GoalPlanTools>();
        services.AddScoped<IncomeSourceTools>();
        services.AddScoped<RecurringTools>();
        services.AddScoped<HouseholdContext>();
        services.AddScoped<HouseholdSettingsTools>();
        services.AddHostedService<BillReminderService>();
        services.AddHostedService<BudgetRolloverService>();
        services.AddScoped<IStatementAiExtractor, PlatformDocumentStatementExtractor>();
        services.AddSingleton<Cortex.Application.Jobs.IJobHandler, StatementParseJobHandler>();
        services.AddSingleton<IModuleToolSource, FinanceToolSource>();
        services.AddHostedService<NetWorthSnapshotService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/finance").WithTags("Finance").RequireAuthorization();

        // Manual bookkeeping from the tab editors (accounts/transactions/budgets/goals CRUD).
        group.MapManualCrudEndpoints();

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
                    interestRateApr = a.InterestRateApr, minimumMonthlyPayment = a.MinimumMonthlyPayment,
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

        group.MapGet("/budgets", async (
                FinanceDbContext db, Cortex.Core.Identity.ICurrentUser currentUser,
                HouseholdContext household, CancellationToken cancellationToken) =>
            {
                if (!BudgetMath.TryParseMonth(null, await household.TodayAsync(cancellationToken), out var period))
                {
                    return Results.Problem("month resolution failed");
                }

                var budgets = await db.Budgets.Where(b => b.PeriodMonth == period).ToListAsync(cancellationToken);
                var categoryNames = await db.Categories.ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
                var monthEnd = period.AddMonths(1).AddDays(-1);
                var visibleIds = (await db.Accounts.ToListAsync(cancellationToken))
                    .Where(a => a.IsVisibleTo(currentUser.UserId))
                    .Select(a => a.Id)
                    .ToHashSet();
                var spentRows = (await db.Transactions
                        .Where(t => t.Direction == "expense" && t.OccurredOn >= period && t.OccurredOn <= monthEnd)
                        .ToListAsync(cancellationToken))
                    .Where(t => visibleIds.Contains(t.AccountId))
                    .ToList();

                return Results.Ok(budgets.Select(b =>
                {
                    var spent = spentRows
                        .Where(t => t.CategoryId == b.CategoryId &&
                                    t.CurrencyCode.Equals(b.CurrencyCode, StringComparison.OrdinalIgnoreCase))
                        .Sum(t => t.Amount);
                    var status = BudgetMath.Status(b.TargetAmount, spent);
                    return new
                    {
                        id = b.Id,
                        categoryName = categoryNames.GetValueOrDefault(b.CategoryId, "(deleted)"),
                        spent,
                        target = b.TargetAmount,
                        remaining = status.Remaining,
                        currencyCode = b.CurrencyCode,
                        status = status.Over ? "OVER" : "on track",
                    };
                }).OrderBy(x => x.categoryName));
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ViewFinance))
            .WithName("Finance_Budgets");

        group.MapGet("/debts", async (
                FinanceDbContext db, Cortex.Core.Identity.ICurrentUser currentUser,
                CancellationToken cancellationToken) =>
            {
                var debts = (await db.Accounts.OrderBy(a => a.Name).ToListAsync(cancellationToken))
                    .Where(a => a.IsVisibleTo(currentUser.UserId) && a.IsDebt && a.CachedBalance < 0);
                return Results.Ok(debts.Select(a => new
                {
                    id = a.Id,
                    name = a.Name,
                    type = a.Type,
                    owed = Math.Abs(a.CachedBalance),
                    apr = a.InterestRateApr,
                    monthlyInterest = a.InterestRateApr is { } apr
                        ? Math.Round(Math.Abs(a.CachedBalance) * apr / 100m / 12m, 2)
                        : (decimal?)null,
                    minimumPayment = a.MinimumMonthlyPayment,
                    currencyCode = a.CurrencyCode,
                }));
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ViewFinance))
            .WithName("Finance_Debts");

        group.MapGet("/net-worth/history", async (FinanceDbContext db, HouseholdContext household, CancellationToken cancellationToken) =>
            {
                // Tenant-level snapshots feed the trend chart — the same series get_net_worth's
                // trend line reports in chat (snapshots are per-currency tenant totals by design).
                var since = (await household.TodayAsync(cancellationToken)).AddDays(-365);
                var snapshots = await db.NetWorthSnapshots
                    .Where(s => s.TakenOn >= since)
                    .OrderBy(s => s.TakenOn)
                    .ToListAsync(cancellationToken);
                return Results.Ok(snapshots.Select(s => new
                {
                    takenOn = s.TakenOn.ToString("yyyy-MM-dd"),
                    netWorth = s.NetWorth,
                    currencyCode = s.CurrencyCode,
                }));
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ViewFinance))
            .WithName("Finance_NetWorthHistory");

        group.MapGet("/recurring", async (
                FinanceDbContext db, Cortex.Core.Identity.ICurrentUser currentUser,
                HouseholdContext household, CancellationToken cancellationToken) =>
            {
                var charges = await RecurringTools.DetectAsync(
                    db, currentUser.UserId, await household.TodayAsync(cancellationToken), cancellationToken);
                return Results.Ok(charges.Select(c => new
                {
                    id = c.MerchantKey,
                    name = c.DisplayName,
                    cadence = c.Cadence,
                    average = c.AverageAmount,
                    monthlyCost = Math.Round(c.MonthlyCost, 2),
                    lastSeen = c.LastSeen.ToString("yyyy-MM-dd"),
                    nextExpected = c.NextExpected.ToString("yyyy-MM-dd"),
                    status = c.PriceRisen ? "price rose" : "steady",
                }));
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ViewFinance))
            .WithName("Finance_Recurring");

        group.MapGet("/income-sources", async (FinanceDbContext db, CancellationToken cancellationToken) =>
            {
                var sources = await db.IncomeSources.OrderBy(i => i.Name).ToListAsync(cancellationToken);
                return Results.Ok(sources.Select(i => new
                {
                    id = i.Id,
                    name = i.Name,
                    amount = i.Amount,
                    cadence = i.Cadence,
                    monthlyEquivalent = Math.Round(i.MonthlyEquivalent, 2),
                    currencyCode = i.CurrencyCode,
                }));
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ViewFinance))
            .WithName("Finance_IncomeSources");

        group.MapGet("/goals", async (
                FinanceDbContext db, Cortex.Core.Identity.ICurrentUser currentUser,
                HouseholdContext household, CancellationToken cancellationToken) =>
            {
                var goals = await db.Goals.OrderBy(g => g.Name).ToListAsync(cancellationToken);
                var accounts = (await db.Accounts.ToListAsync(cancellationToken))
                    .Where(a => a.IsVisibleTo(currentUser.UserId))
                    .ToDictionary(a => a.Id);
                var today = await household.TodayAsync(cancellationToken);
                return Results.Ok(goals.Select(g =>
                {
                    var saved = GoalTools.GoalProgress(g, accounts);
                    return new
                    {
                        id = g.Id,
                        name = g.Name,
                        saved = saved is { } s ? s.ToString("N2") : "(private account)",
                        target = g.TargetAmount,
                        progress = saved is { } p ? GoalMath.Percent(p, g.TargetAmount).ToString("P0") : "—",
                        currencyCode = g.CurrencyCode,
                        targetDate = g.TargetDate?.ToString("yyyy-MM-dd"),
                        pace = g.TargetDate is { } d && saved is { } v
                            ? (GoalMath.OnPace(v, g.TargetAmount, g.CreatedAt, d, today) ? "on pace" : "behind")
                            : null,
                    };
                }));
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ViewFinance))
            .WithName("Finance_Goals");

        // ── Statement review: the import pipeline's human moment, as a working surface ──
        group.MapGet("/imports/latest/lines", async (FinanceDbContext db, CancellationToken cancellationToken) =>
            {
                var batch = await db.ImportBatches
                    .OrderByDescending(b => b.CreatedAt)
                    .FirstOrDefaultAsync(cancellationToken);
                if (batch is null || batch.Status != "parsed")
                {
                    return Results.Ok(Array.Empty<object>()); // the tab shows its placeholder
                }

                var lines = StatementImportTools.Deserialize(batch.ExtractedLinesJson);
                return Results.Ok(lines.Select((line, i) => new
                {
                    index = i,
                    date = line.Date.ToString("yyyy-MM-dd"),
                    description = line.Description,
                    amount = (line.Direction == "income" ? "+" : "-") + line.Amount.ToString("N2"),
                    direction = line.Direction,
                    category = line.SuggestedCategory,
                    batch = batch.FileName,
                }));
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ViewFinance))
            .WithName("Finance_ImportLines");

        group.MapPost("/imports/latest/lines", async (
                ReviewLineRequest body, FinanceDbContext db, CancellationToken cancellationToken) =>
            {
                var batch = await db.ImportBatches
                    .OrderByDescending(b => b.CreatedAt)
                    .FirstOrDefaultAsync(cancellationToken);
                if (batch is null || batch.Status != "parsed")
                {
                    return Results.BadRequest(new { error = "No batch is awaiting review." });
                }

                var lines = StatementImportTools.Deserialize(batch.ExtractedLinesJson).ToList();
                if (body.Index < 0 || body.Index >= lines.Count)
                {
                    return Results.BadRequest(new { error = $"Line {body.Index} does not exist." });
                }

                string? category = null;
                if (!string.IsNullOrWhiteSpace(body.Category))
                {
                    var match = await db.Categories.FirstOrDefaultAsync(
                        c => EF.Functions.ILike(c.Name, body.Category.Trim()), cancellationToken);
                    if (match is null)
                    {
                        return Results.BadRequest(new { error = $"No category named '{body.Category}' exists." });
                    }

                    category = match.Name;
                }

                lines[body.Index] = lines[body.Index] with { SuggestedCategory = category };
                batch.ExtractedLinesJson = System.Text.Json.JsonSerializer.Serialize(
                    lines, System.Text.Json.JsonSerializerOptions.Web);
                await db.SaveChangesAsync(cancellationToken);
                return Results.Ok(new { index = body.Index, category });
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ReviewImports))
            .WithName("Finance_ReviewLine");

        group.MapDelete("/imports/latest/lines/{index:int}", async (
                int index, FinanceDbContext db, CancellationToken cancellationToken) =>
            {
                var batch = await db.ImportBatches
                    .OrderByDescending(b => b.CreatedAt)
                    .FirstOrDefaultAsync(cancellationToken);
                if (batch is null || batch.Status != "parsed")
                {
                    return Results.BadRequest(new { error = "No batch is awaiting review." });
                }

                var lines = StatementImportTools.Deserialize(batch.ExtractedLinesJson).ToList();
                if (index < 0 || index >= lines.Count)
                {
                    return Results.NotFound();
                }

                lines.RemoveAt(index);
                batch.ExtractedLinesJson = System.Text.Json.JsonSerializer.Serialize(
                    lines, System.Text.Json.JsonSerializerOptions.Web);
                await db.SaveChangesAsync(cancellationToken);
                return Results.NoContent();
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ReviewImports))
            .WithName("Finance_DropLine");

        group.MapPost("/imports/latest/approve", async (
                StatementImportTools imports, CancellationToken cancellationToken) =>
            {
                // The reviewer clicking the button IS the human approval this batch waits for.
                var message = await imports.ApproveImportBatch(fileName: null, cancellationToken);
                return Results.Ok(new { message });
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ReviewImports))
            .WithName("Finance_ApproveLatestImport");

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

/// <summary>Body of the review tab's line edit: which line, and its corrected category (empty clears it).</summary>
public sealed record ReviewLineRequest(int Index, string? Category);

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

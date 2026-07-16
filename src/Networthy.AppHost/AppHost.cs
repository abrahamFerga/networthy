// Networthy local orchestration — the full Plenipo-based stack in one command:
//   dotnet run --project src/Networthy.AppHost    (or `aspire run`)
//
// Zero-config: the chat assistant uses Plenipo's dependency-free Mock provider and the RAG
// pipeline uses the deterministic Mock embedder, so no API keys are required. The workspace UI
// is Networthy's OWN entry (frontend/networthy-ui — the Plenipo shell + the custom Overview
// dashboard, ADR-0008), launched as a Vite dev server; the admin console still comes straight
// from a sibling Plenipo checkout (default ../Plenipo next to this repo; override with
// "PlenipoRepoPath" in appsettings or user-secrets). networthy-ui itself needs no checkout —
// @plenipo/ui installs from npm (ADR-0008, amended). No checkout found → API runs, admin skipped.

var builder = DistributedApplication.CreateBuilder(args);

// STABLE dev password (overridable via Parameters:plenipo-pg-password in user-secrets). Postgres
// bakes the password into the data volume at first init and never re-reads it — with Aspire's
// default *generated* password, losing/regenerating user-secrets leaves the volume unopenable
// ("28P01 password authentication failed", the API waits forever, the console shows nothing).
// A fixed dev default can't drift. Local demo container only — not a production credential.
var pgPassword = builder.AddParameter("plenipo-pg-password", "networthy-dev-only", secret: true);

var postgres = builder.AddPostgres("plenipo-pg", password: pgPassword)
    // pgvector-enabled Postgres — Plenipo's opt-in RAG pipeline needs the vector extension at
    // migration time. pg17 pairs with Aspire's data-volume mount (see the Plenipo AppHost notes:
    // a volume created by a different Postgres major needs `docker volume rm` to reset).
    .WithImage("pgvector/pgvector")
    .WithImageTag("pg17")
    .WithDataVolume();

var platformDb = postgres.AddDatabase("plenipo-platform");
var auditDb = postgres.AddDatabase("plenipo-audit");

var redis = builder.AddRedis("plenipo-redis");

// Deployment defaults stay keyless. Commercial provider credentials are configured per tenant
// under Admin → AI Settings and stored write-only in Plenipo's secret vault.
var aiProvider = builder.AddParameter("ai-provider", "Mock", publishValueAsDefault: true);
var aiModel = builder.AddParameter("ai-model", "gpt-4o-mini", publishValueAsDefault: true);

var api = builder.AddProject<Projects.Networthy_Host>("networthy-api")
    .WithReference(platformDb)
    .WithReference(auditDb)
    .WithReference(redis)
    .WaitFor(platformDb)
    .WaitFor(auditDb)
    .WithEnvironment("Ai__Provider", aiProvider)
    .WithEnvironment("Ai__Model", aiModel)
    .WithExternalHttpEndpoints();

// ── Front-ends (Vite dev servers; the workspace is THIS repo's entry, not the stock shell) ──
var plenipoRepo = Path.GetFullPath(
    builder.Configuration["PlenipoRepoPath"] ?? Path.Combine(builder.AppHostDirectory, "..", "..", "..", "Plenipo"));
// The workspace dev server MUST run frontend/networthy-ui — the stock @plenipo/ui harness has no
// Overview registration, so the dashboard tab would crash into the generic table renderer.
var workspaceDir = Path.Combine(builder.AppHostDirectory, "..", "..", "frontend", "networthy-ui");
var adminDir = Path.Combine(plenipoRepo, "frontend", "admin-ui");

// networthy-ui needs only pnpm — @plenipo/ui installs from npm (ADR-0008, amended), so it no
// longer depends on a checkout. Only the admin console does; gate the two separately, or a missing
// checkout would silently cost you the workspace dev server too.
if (builder.ExecutionContext.IsRunMode && ToolExistsOnPath("pnpm"))
{
    var workspace = builder.AddViteApp("networthy-ui", workspaceDir)
        .WithPnpm()
        .WaitFor(api)
        .WithEnvironment("VITE_BRAND_NAME", "Networthy")
        .WithEnvironment("VITE_API_BASE", api.GetEndpoint("http"))
        .WithExternalHttpEndpoints();

    // Teach the API's CORS policy the front-end origins (ports are assigned dynamically); the
    // fixed localhost ports cover running `pnpm dev` outside Aspire. Indices must stay GAPLESS —
    // IConfiguration binds Cors:Origins:N as an array and stops at the first missing index, so the
    // admin origin is numbered inline rather than reserving a slot that may go unfilled.
    var corsIndex = 0;
    api.WithEnvironment($"Cors__Origins__{corsIndex++}", workspace.GetEndpoint("http"));

    if (Directory.Exists(adminDir))
    {
        var admin = builder.AddViteApp("networthy-admin-ui", adminDir)
            .WithPnpm()
            .WaitFor(api)
            .WithEnvironment("VITE_API_BASE", api.GetEndpoint("http"))
            .WithEnvironment("VITE_WORKSPACE_URL", workspace.GetEndpoint("http"))
            .WithExternalHttpEndpoints();

        // The workspace's "Admin" link targets the admin console (Vite serves it under its /admin base).
        workspace.WithEnvironment(
            "VITE_ADMIN_URL",
            ReferenceExpression.Create($"{admin.GetEndpoint("http")}/admin"));

        api.WithEnvironment($"Cors__Origins__{corsIndex++}", admin.GetEndpoint("http"));
    }
    else
    {
        Console.WriteLine(
            $"[Networthy.AppHost] Admin console dev server skipped — no Plenipo checkout at " +
            $"'{plenipoRepo}' (set \"PlenipoRepoPath\"). networthy-ui still runs, and the API still " +
            "serves the committed admin bundle from wwwroot/admin.");
    }

    api.WithEnvironment($"Cors__Origins__{corsIndex++}", "http://localhost:5173")
       .WithEnvironment($"Cors__Origins__{corsIndex++}", "http://localhost:5174");
}
else if (builder.ExecutionContext.IsRunMode)
{
    Console.WriteLine(
        "[Networthy.AppHost] UI dev servers skipped — pnpm is not on PATH (`corepack enable`). " +
        "The API still runs and serves the committed bundles from wwwroot.");
}

builder.Build().Run();

// True when `tool` resolves on PATH (Windows launchers included — pnpm installs as pnpm.cmd).
static bool ToolExistsOnPath(string tool)
{
    var extensions = OperatingSystem.IsWindows() ? new[] { ".cmd", ".exe", ".bat", "" } : new[] { "" };
    return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .SelectMany(_ => extensions, (dir, ext) => Path.Combine(dir.Trim('"'), tool + ext))
        .Any(File.Exists);
}

using Cortex.Infrastructure.Context;
using Cortex.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace Networthy.IntegrationTests;

/// <summary>
/// The real host on a throwaway Postgres: platform + finance migrations run, the dev tenant and
/// category taxonomy seed, the job processor and hosted services start. Everything is real
/// except the AI provider (Mock) — the same keyless posture the Cortex platform's own suite uses.
/// </summary>
public sealed class IntegrationFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;

    public WebApplicationFactory<Program> Factory { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("TESTCONTAINERS_RYUK_DISABLED", "true");

        _postgres = new PostgreSqlBuilder()
            .WithImage("pgvector/pgvector:pg16") // the platform's RAG migration needs the vector extension
            .WithDatabase("cortex_platform")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();
        await _postgres.StartAsync();

        Environment.SetEnvironmentVariable("ConnectionStrings__cortex-platform", _postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("ConnectionStrings__cortex-audit", _postgres.GetConnectionString());

        Factory = new NetworthyAppFactory();

        // First request boots the host (migrations + seeding); the authenticated call makes the
        // request enricher provision the dev-tenant user that AuthorizedScopeAsync relies on.
        using var warmup = AdminClient();
        (await warmup.GetAsync("/alive")).EnsureSuccessStatusCode();
        (await warmup.GetAsync("/api/platform/modules")).EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__cortex-platform", null);
        Environment.SetEnvironmentVariable("ConnectionStrings__cortex-audit", null);

        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    /// <summary>An authorized HTTP client for the dev tenant's admin.</summary>
    public HttpClient AdminClient()
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Subject", "it-admin");
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        client.DefaultRequestHeaders.Add("X-Dev-Roles", "system_admin");
        return client;
    }

    /// <summary>A DI scope with tenant + user + permissions populated — how module tools run
    /// after the platform's auth/approval pipeline has done its part.</summary>
    public async Task<(IServiceScope Scope, Guid TenantId, Guid UserId)> AuthorizedScopeAsync()
    {
        var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
        var tenant = await db.Tenants.FirstAsync(t => t.Slug == "dev");
        context.SetTenant(tenant.Id);
        var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.TenantId == tenant.Id);
        context.SetUser(user.Id, user.Subject, user.DisplayName);
        context.SetPermissions(["*"]);
        return (scope, tenant.Id, user.Id);
    }

    private sealed class NetworthyAppFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
        }
    }
}

[CollectionDefinition("api")]
public sealed class ApiCollection : ICollectionFixture<IntegrationFixture>;

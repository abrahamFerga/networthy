using Cortex.Connectors.Sdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Networthy.Connectors.Plaid;

/// <summary>
/// The product-owned Plaid connector (ADR-0007): bank-account linking and transaction sync,
/// defined in the Networthy repo against the Cortex connector SDK — not in Cortex core, because
/// bank linking is finance-domain-specific. Service-auth (ADR-0006): the household admin
/// configures the Plaid credentials as tenant-level settings under Integrations; the platform
/// stores secrets write-only and protected at rest. Bank linking is always OPT-IN — statement
/// upload remains the first-class, credential-free path (SPEC.md differentiator #3).
/// </summary>
public sealed class PlaidConnector : IConnector
{
    public const string ConnectorId = "plaid";

    public ConnectorManifest Manifest { get; } = new()
    {
        Id = ConnectorId,
        DisplayName = "Plaid bank linking",
        Description = "Opt-in live bank-account sync via Plaid — an alternative to statement upload, never a requirement.",
        AuthMode = ConnectorAuthMode.Service,
        Icon = "landmark",
        Settings =
        [
            new ConnectorSettingDescriptor
            {
                Key = "ClientId",
                Label = "Plaid client id",
                Required = true,
            },
            new ConnectorSettingDescriptor
            {
                Key = "ClientSecret",
                Label = "Plaid secret",
                Description = "Stored protected; never shown again after saving.",
                Required = true,
                IsSecret = true,
            },
            new ConnectorSettingDescriptor
            {
                Key = "Environment",
                Label = "Plaid environment",
                Description = "sandbox, development, or production (default sandbox).",
            },
            new ConnectorSettingDescriptor
            {
                Key = "AccessToken",
                Label = "Item access token",
                Description = "The linked item's access token (from Plaid Link). Stored protected.",
                Required = true,
                IsSecret = true,
            },
        ],
        // Tools land with the bank-linking feature (issue #13): list_plaid_accounts,
        // sync_plaid_transactions (approval-gated — imports external data).
        Tools = [],
    };

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        // Client seam + tools register with the bank-linking feature (issue #13).
    }
}

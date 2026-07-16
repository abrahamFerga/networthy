using Plenipo.Core.Entities;

namespace Networthy.Finance.Persistence;

/// <summary>
/// The binding between a Plaid account (on the household's linked item) and a Networthy
/// <see cref="Account"/> — where synced transactions post. The Plaid access token itself is
/// NOT here: it lives in the connector's tenant-level settings, write-only (ADR-0006).
/// </summary>
public sealed class PlaidLinkedAccount : TenantEntityBase
{
    public Guid AccountId { get; set; }

    /// <summary>Plaid's stable account_id within the linked item (PII-adjacent identifier).</summary>
    public required string PlaidAccountId { get; set; }

    public DateTimeOffset? LastSyncedAt { get; set; }
}

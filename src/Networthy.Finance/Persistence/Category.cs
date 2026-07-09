using Cortex.Core.Entities;

namespace Networthy.Finance.Persistence;

/// <summary>
/// A spending/income category — the vocabulary transactions and budgets speak. Each household
/// (tenant) gets the starter taxonomy seeded on first run and curates it from there (the same
/// seeded-then-curated pattern as Casewell's clause library). Optional parent for subcategories.
/// </summary>
public sealed class Category : TenantEntityBase
{
    public required string Name { get; set; }

    /// <summary>Optional parent for subcategories (e.g. "Dining" under "Food").</summary>
    public Guid? ParentCategoryId { get; set; }
}

using Networthy.Finance;

namespace Networthy.Finance.Tests;

/// <summary>
/// Foundations guards: the manifest is coherent (id, tabs, permissions) and the starter
/// taxonomy every household seeds from stays sane. The pinned tool list grows feature by
/// feature — each feature adds its tools here and asserts their approval gating.
/// </summary>
public sealed class FinanceCatalogTests
{
    [Fact]
    public void Manifest_DeclaresTheFinanceModule()
    {
        var manifest = new FinanceModule().Manifest;

        Assert.Equal("finance", manifest.Id);
        Assert.Equal("Finance", manifest.DisplayName);
        Assert.Contains(manifest.Tabs, t => t.Id == "chat");
        Assert.Contains(manifest.Tabs, t => t.Id == "categories" && t.Editor is not null);

        // The categories editor is admin-gated; viewing is household-wide.
        var categories = manifest.Tabs.Single(t => t.Id == "categories");
        Assert.Equal(FinanceModule.ViewFinance, categories.Permission);
        Assert.Equal(FinanceModule.ManageCategories, categories.Editor!.Permission);
    }

    [Fact]
    public void Manifest_ToolList_IsPinned()
    {
        var manifest = new FinanceModule().Manifest;

        // Foundations ships no tools; features add theirs here as they land.
        Assert.Equal([], manifest.Tools.Select(t => t.Name));
    }

    [Fact]
    public void StarterCategories_AreUniqueAndNonEmpty()
    {
        Assert.NotEmpty(FinanceCatalog.StarterCategories);
        Assert.Equal(
            FinanceCatalog.StarterCategories.Count,
            FinanceCatalog.StarterCategories.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(FinanceCatalog.StarterCategories, c => Assert.False(string.IsNullOrWhiteSpace(c)));
        // The taxonomy must cover both directions of money.
        Assert.Contains("Salary", FinanceCatalog.StarterCategories);
        Assert.Contains("Groceries", FinanceCatalog.StarterCategories);
    }
}

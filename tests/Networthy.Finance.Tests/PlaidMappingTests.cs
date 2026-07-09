using Networthy.Connectors.Plaid;

namespace Networthy.Finance.Tests;

/// <summary>
/// The Plaid connector's pure rules: Plaid's inverted sign convention (positive = money OUT),
/// subtype → account-type mapping, dedupe identity, environment → host resolution, and the
/// manifest guard (tools that import external data are approval-gated).
/// </summary>
public sealed class PlaidMappingTests
{
    [Theory]
    [InlineData(82.45, 82.45, "expense")]   // Plaid positive = outflow
    [InlineData(-2500, 2500, "income")]     // Plaid negative = inflow
    [InlineData(0, 0, "expense")]
    public void ToNetworthy_FlipsPlaidSignConvention(double plaid, double amount, string direction)
    {
        Assert.Equal(((decimal)amount, direction), PlaidMapping.ToNetworthy((decimal)plaid));
    }

    [Theory]
    [InlineData("checking", "checking")]
    [InlineData("Credit Card", "credit")]
    [InlineData("money market", "savings")]
    [InlineData("brokerage", "checking")]   // unknown subtypes default safely
    [InlineData(null, "checking")]
    public void ToAccountType_MapsSubtypes(string? subtype, string expected)
    {
        Assert.Equal(expected, PlaidMapping.ToAccountType(subtype));
    }

    [Fact]
    public void DedupeKey_NormalizesCaseAndWhitespace()
    {
        var a = PlaidMapping.DedupeKey(new DateOnly(2026, 7, 9), 6.50m, " Blue Bottle ");
        var b = PlaidMapping.DedupeKey(new DateOnly(2026, 7, 9), 6.5m, "BLUE BOTTLE");
        Assert.Equal(a, b);
        Assert.NotEqual(a, PlaidMapping.DedupeKey(new DateOnly(2026, 7, 10), 6.50m, "Blue Bottle"));
    }

    [Theory]
    [InlineData("sandbox", "https://sandbox.plaid.com")]
    [InlineData("Production", "https://production.plaid.com")]
    [InlineData("whatever", "https://sandbox.plaid.com")]   // unknown env falls back to sandbox
    public void Connection_ResolvesEnvironmentHost(string environment, string expected)
    {
        Assert.Equal(expected, new PlaidConnection("id", "secret", environment, "token").BaseUrl);
    }

    [Fact]
    public void Manifest_GatesTheImportingTools()
    {
        var manifest = new PlaidConnector().Manifest;

        Assert.Equal("plaid", manifest.Id);
        Assert.Equal(
            ["list_plaid_accounts", "link_plaid_account", "sync_plaid_transactions"],
            manifest.Tools.Select(t => t.Name));
        Assert.All(
            manifest.Tools.Where(t => t.Name is "link_plaid_account" or "sync_plaid_transactions"),
            t => Assert.True(t.RequiresApproval));
        // Credentials are secrets, write-only at the platform layer.
        Assert.All(
            manifest.Settings.Where(s => s.Key is "ClientSecret" or "AccessToken"),
            s => Assert.True(s.IsSecret));
    }
}

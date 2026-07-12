using Networthy.Finance;
using Xunit;

namespace Networthy.Finance.Tests;

/// <summary>
/// The settings/onboarding pickers offer runtime-derived vocabularies instead of free text —
/// these pin that the derivation actually yields the values households need, on every OS the
/// host runs on (Windows dev boxes enumerate Windows zone ids; they must convert to IANA).
/// </summary>
public class SettingsOptionsTests
{
    [Theory]
    [InlineData("USD")]
    [InlineData("MXN")]
    [InlineData("EUR")]
    public void CurrencyOptions_CarryTheCodesHouseholdsUse(string code) =>
        Assert.Contains(code, FinanceModule.CurrencyCodes);

    [Fact]
    public void CurrencyOptions_AreUppercaseIsoShaped_AndSorted()
    {
        Assert.All(FinanceModule.CurrencyCodes, c =>
        {
            Assert.Equal(3, c.Length);
            Assert.All(c, ch => Assert.True(char.IsAsciiLetterUpper(ch)));
        });
        Assert.Equal(FinanceModule.CurrencyCodes.OrderBy(c => c, StringComparer.Ordinal), FinanceModule.CurrencyCodes);
        Assert.Equal(FinanceModule.CurrencyCodes.Distinct(), FinanceModule.CurrencyCodes);
    }

    [Fact]
    public void TimeZoneOptions_AreIanaIds_NotWindowsDisplayNames()
    {
        // IANA ids are Area/Location ("America/Mexico_City"); Windows ids contain spaces
        // ("Central Standard Time (Mexico)"). A single space in the list means the
        // Windows→IANA conversion regressed and stored values would stop resolving on Linux.
        Assert.Contains("America/Mexico_City", FinanceModule.TimeZoneIds);
        Assert.DoesNotContain(FinanceModule.TimeZoneIds, id => id.Contains(' '));
    }

    [Fact]
    public void TimeZoneOptions_AllResolve_OnThisRuntime() =>
        Assert.All(FinanceModule.TimeZoneIds, id =>
            Assert.True(TimeZoneInfo.TryFindSystemTimeZoneById(id, out _), $"'{id}' does not resolve"));
}

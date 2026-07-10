using Networthy.Finance;
using static Networthy.Finance.RecurringDetection;

namespace Networthy.Finance.Tests;

public class RecurringDetectionTests
{
    private static Observation Obs(string date, double amount, string description) =>
        new(DateOnly.Parse(date), (decimal)amount, description);

    [Fact]
    public void Monthly_subscription_is_detected_across_reference_number_noise()
    {
        var charges = Detect(
        [
            Obs("2026-03-05", 15.49, "NETFLIX.COM 880-1234"),
            Obs("2026-04-05", 15.49, "NETFLIX.COM 880-5678"),
            Obs("2026-05-06", 15.49, "Netflix.com 880-9012"),
            Obs("2026-06-05", 17.99, "NETFLIX.COM 880-3456"), // price rise, still the same bill
        ]);

        var netflix = Assert.Single(charges);
        Assert.Equal("monthly", netflix.Cadence);
        Assert.Equal(4, netflix.Occurrences);
        Assert.True(netflix.PriceRisen);                        // 17.99 > avg·1.08
        Assert.Equal(new DateOnly(2026, 7, 6), netflix.NextExpected); // last + median gap (31d)
    }

    [Fact]
    public void Same_store_wild_amounts_or_wild_gaps_are_shopping_not_bills()
    {
        // Groceries: right store, wrong rhythm and swinging amounts.
        var groceries = Detect(
        [
            Obs("2026-06-01", 84.12, "WHOLE FOODS MARKET"),
            Obs("2026-06-04", 23.50, "WHOLE FOODS MARKET"),
            Obs("2026-06-28", 141.80, "WHOLE FOODS MARKET"),
            Obs("2026-07-01", 61.00, "WHOLE FOODS MARKET"),
        ]);
        Assert.Empty(groceries);

        // Two occurrences are never enough, however regular.
        Assert.Empty(Detect([Obs("2026-05-01", 9.99, "SPOTIFY"), Obs("2026-06-01", 9.99, "SPOTIFY")]));
    }

    [Fact]
    public void Weekly_and_yearly_rhythms_classify_with_correct_monthly_costs()
    {
        var charges = Detect(
        [
            Obs("2026-06-06", 12.00, "CLEANERS SERVICE"),
            Obs("2026-06-13", 12.00, "CLEANERS SERVICE"),
            Obs("2026-06-20", 12.00, "CLEANERS SERVICE"),
            Obs("2026-06-27", 12.00, "CLEANERS SERVICE"),
            Obs("2024-07-01", 120.00, "AMAZON PRIME ANNUAL"),
            Obs("2025-07-01", 120.00, "AMAZON PRIME ANNUAL"),
            Obs("2026-07-01", 139.00, "AMAZON PRIME ANNUAL"),
        ]);

        Assert.Equal(2, charges.Count);
        var weekly = charges.Single(c => c.Cadence == "weekly");
        Assert.Equal(52m, Math.Round(weekly.MonthlyCost, 0)); // 12·52/12
        var yearly = charges.Single(c => c.Cadence == "yearly");
        Assert.InRange(yearly.MonthlyCost, 10m, 11m);         // ~126.33/12
        // Costliest-first ordering: the weekly 52/month outranks the yearly ~10.5/month.
        Assert.Equal("weekly", charges[0].Cadence);
    }

    [Fact]
    public void Merchant_normalization_strips_noise_not_identity()
    {
        Assert.Equal(NormalizeMerchant("NETFLIX.COM 880-1234"), NormalizeMerchant("netflix.com #880*9999"));
        Assert.NotEqual(NormalizeMerchant("NETFLIX.COM"), NormalizeMerchant("SPOTIFY AB"));
    }
}

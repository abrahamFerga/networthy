using Networthy.Finance;
using Networthy.Finance.Persistence;
using Xunit;

namespace Networthy.Finance.Tests;

/// <summary>
/// The statement reminder's pure decisions (issue #71): which period a run belongs to, whether the
/// previous period's income was steady enough to offer a one-click confirm, and the two shapes the
/// nudge takes. The handler around it only fetches rows, scopes them to each member's visibility,
/// and sends — everything decision-shaped is pinned here without a database (as with the digest).
/// </summary>
public class StatementReminderTests
{
    [Fact]
    public void PeriodStart_Monthly_IsFirstOfTheMonth() =>
        Assert.Equal(new DateOnly(2026, 7, 1),
            StatementReminderPlan.PeriodStart(new DateOnly(2026, 7, 23), "monthly"));

    [Theory]
    [InlineData("2026-07-23", "2026-07-20")] // a Thursday -> that week's Monday
    [InlineData("2026-07-20", "2026-07-20")] // a Monday is its own period start
    [InlineData("2026-07-19", "2026-07-13")] // a Sunday belongs to the week that began Monday the 13th
    public void PeriodStart_Weekly_IsThisWeeksMonday(string today, string expected) =>
        Assert.Equal(DateOnly.Parse(expected),
            StatementReminderPlan.PeriodStart(DateOnly.Parse(today), "weekly"));

    [Fact]
    public void PreviousPeriodStart_StepsBackOnePeriod()
    {
        Assert.Equal(new DateOnly(2026, 6, 1),
            StatementReminderPlan.PreviousPeriodStart(new DateOnly(2026, 7, 1), "monthly"));
        Assert.Equal(new DateOnly(2026, 7, 13),
            StatementReminderPlan.PreviousPeriodStart(new DateOnly(2026, 7, 20), "weekly"));
    }

    [Fact]
    public void IncomeIsConsistent_WithinTolerance_IsSteady()
    {
        // Declared ≈4,800/month; last month brought in 4,750 — a payday that slid a day, still steady.
        Assert.True(StatementReminderPlan.IncomeIsConsistent(expectedPeriodIncome: 4800m, actualPeriodIncome: 4750m));
    }

    [Fact]
    public void IncomeIsConsistent_OutsideTolerance_IsIrregular()
    {
        // Only half the expected income landed — a genuinely irregular month; fall back to upload.
        Assert.False(StatementReminderPlan.IncomeIsConsistent(expectedPeriodIncome: 4800m, actualPeriodIncome: 2400m));
    }

    [Fact]
    public void IncomeIsConsistent_NoDeclaration_IsNeverSteady()
    {
        // Nothing declared (expected 0): there is no steady schedule to roll forward.
        Assert.False(StatementReminderPlan.IncomeIsConsistent(expectedPeriodIncome: 0m, actualPeriodIncome: 4800m));
    }

    [Fact]
    public void IncomeIsConsistent_NoInflow_IsNeverSteady()
    {
        // Declared income but none actually arrived last period: ask for a statement, don't confirm.
        Assert.False(StatementReminderPlan.IncomeIsConsistent(expectedPeriodIncome: 4800m, actualPeriodIncome: 0m));
    }

    [Fact]
    public void Compose_ConsistentIncome_OffersOneClickConfirm_NotAnUpload()
    {
        var notice = StatementReminderPlan.Compose(
            new DateOnly(2026, 7, 1), "monthly", incomeConsistent: true, expectedPeriodIncome: 4800m, "USD");

        Assert.True(notice.AutoConfirm);
        Assert.Equal(StatementReminderPlan.Category, notice.Category);
        Assert.Contains("Confirm", notice.Title);
        Assert.Contains("roll it forward", notice.Body);
        Assert.Equal("/finance/income", notice.Link); // the confirm path, not the upload gate
    }

    [Fact]
    public void Compose_IrregularIncome_FallsBackToTheUploadFlow()
    {
        var notice = StatementReminderPlan.Compose(
            new DateOnly(2026, 7, 1), "monthly", incomeConsistent: false, expectedPeriodIncome: 0m, "USD");

        Assert.False(notice.AutoConfirm);
        Assert.Equal(StatementReminderPlan.Category, notice.Category);
        Assert.Contains("Upload", notice.Title);
        Assert.Contains("approve", notice.Body); // the review -> approve gate is named, never bypassed
        Assert.Equal("/finance/review", notice.Link);
    }

    [Fact]
    public void PeriodIncomeFactor_ScalesMonthlyToTheWeek()
    {
        Assert.Equal(1m, StatementReminderPlan.PeriodIncomeFactor("monthly"));
        Assert.Equal(12m / 52m, StatementReminderPlan.PeriodIncomeFactor("weekly"));
    }

    [Theory]
    [InlineData("monthly", "monthly")]
    [InlineData("Monthly", "monthly")]
    [InlineData("every month", "monthly")]
    [InlineData("weekly", "weekly")]
    [InlineData("every week", "weekly")]
    [InlineData("biweekly", null)] // statements aren't biweekly — only the two the setting speaks
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeStatementCadence_SpeaksOnlyMonthlyAndWeekly(string? input, string? expected) =>
        Assert.Equal(expected, HouseholdSettings.NormalizeStatementCadence(input));
}

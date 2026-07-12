namespace Networthy.Finance.Tests;

/// <summary>
/// The Recurring calendar's projection contract (issue #46): detection stays
/// <see cref="RecurringDetection"/>'s alone; the projection only repeats each charge's next
/// expected date forward by its cadence inside the window, so a 60-day calendar shows a
/// monthly bill twice and a weekly one every week — and a just-missed bill doesn't vanish.
/// </summary>
public class BillsCalendarMathTests
{
    private static readonly DateOnly Today = new(2026, 7, 11);
    private static readonly DateOnly Until = Today.AddDays(60); // 2026-09-09

    private static RecurringCharge Charge(string name, string cadence, DateOnly nextExpected, decimal average = 15.49m) =>
        new(name.ToUpperInvariant(), name, cadence, average, average, nextExpected.AddDays(-30), nextExpected, 3);

    [Fact]
    public void A_monthly_bill_lands_twice_inside_sixty_days()
    {
        var occurrences = BillsCalendarMath.Project(
            [Charge("Netflix", "monthly", new DateOnly(2026, 7, 15))], Today, Until);

        Assert.Equal(
            [new DateOnly(2026, 7, 15), new DateOnly(2026, 8, 15)],
            occurrences.Select(o => o.DueOn).ToList());
        Assert.All(occurrences, o => Assert.Equal("Netflix", o.Name));
        Assert.All(occurrences, o => Assert.Equal(15.49m, o.Amount));
    }

    [Fact]
    public void A_weekly_bill_repeats_every_seven_days_to_the_window_edge()
    {
        var occurrences = BillsCalendarMath.Project(
            [Charge("Cleaner", "weekly", new DateOnly(2026, 7, 14), 45m)], Today, Until);

        Assert.Equal(9, occurrences.Count); // Jul 14 … Sep 8 in 7-day steps
        Assert.Equal(new DateOnly(2026, 7, 14), occurrences[0].DueOn);
        Assert.All(occurrences.Zip(occurrences.Skip(1)),
            pair => Assert.Equal(7, pair.Second.DueOn.DayNumber - pair.First.DueOn.DayNumber));
    }

    [Fact]
    public void A_bill_expected_before_today_surfaces_at_its_next_on_rhythm_date()
    {
        // Detection dated it three days ago (late or just charged); the calendar shows the
        // NEXT cycles instead of dropping the bill for a month.
        var occurrences = BillsCalendarMath.Project(
            [Charge("Gym", "monthly", Today.AddDays(-3), 60m)], Today, Until);

        Assert.Equal(
            [new DateOnly(2026, 8, 8), new DateOnly(2026, 9, 8)],
            occurrences.Select(o => o.DueOn).ToList());
    }

    [Fact]
    public void A_bill_beyond_the_window_stays_off_the_calendar()
    {
        var occurrences = BillsCalendarMath.Project(
            [Charge("Insurance", "yearly", Until.AddDays(5))], Today, Until);

        Assert.Empty(occurrences);
    }

    [Fact]
    public void Occurrences_come_out_soonest_first_across_charges()
    {
        var occurrences = BillsCalendarMath.Project(
        [
            Charge("Netflix", "monthly", new DateOnly(2026, 8, 1)),
            Charge("Water", "monthly", new DateOnly(2026, 7, 20), 30m),
        ], Today, Until);

        Assert.Equal(occurrences.OrderBy(o => o.DueOn).Select(o => o.DueOn), occurrences.Select(o => o.DueOn));
        Assert.Equal("Water", occurrences[0].Name);
    }
}

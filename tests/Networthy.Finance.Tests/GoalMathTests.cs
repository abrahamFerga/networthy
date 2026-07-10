using Networthy.Finance;

namespace Networthy.Finance.Tests;

public class GoalMathTests
{
    [Theory]
    [InlineData(0, 1000, 0.0)]
    [InlineData(250, 1000, 0.25)]
    [InlineData(1500, 1000, 1.0)] // overshoot clamps — progress never reads as 150%
    public void Percent_is_clamped_share_of_target(decimal saved, decimal target, double expected) =>
        Assert.Equal((decimal)expected, GoalMath.Percent(saved, target));

    [Fact]
    public void Percent_of_a_nonpositive_target_is_zero_not_a_division_crash() =>
        Assert.Equal(0, GoalMath.Percent(100, 0));

    [Fact]
    public void OnPace_compares_against_the_straight_line()
    {
        var created = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var deadline = new DateOnly(2026, 12, 31);

        // Halfway through the year, half saved = on pace; less = behind.
        var midYear = new DateOnly(2026, 7, 2);
        Assert.True(GoalMath.OnPace(saved: 500, target: 1000, created, deadline, midYear));
        Assert.False(GoalMath.OnPace(saved: 300, target: 1000, created, deadline, midYear));
    }

    [Fact]
    public void OnPace_after_the_deadline_means_actually_done()
    {
        var created = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var deadline = new DateOnly(2026, 6, 1);
        var after = new DateOnly(2026, 7, 1);

        Assert.False(GoalMath.OnPace(saved: 999, target: 1000, created, deadline, after));
        Assert.True(GoalMath.OnPace(saved: 1000, target: 1000, created, deadline, after));
    }
}

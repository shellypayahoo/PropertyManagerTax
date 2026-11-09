using PropertyManager.Core.Configuration;

namespace PropertyManager.Tests;

public class RelativeTimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 18, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0.2, "just now")]
    [InlineData(25, "25 min ago")]
    [InlineData(60, "1 hour ago")]
    [InlineData(185, "3 hours ago")]
    [InlineData(30 * 60, "yesterday")]
    [InlineData(5 * 24 * 60, "5 days ago")]
    public void Past_times_read_the_same_in_any_time_zone(double minutesAgo, string expected) =>
        Assert.Equal(expected, RelativeTime.Ago(Now.AddMinutes(-minutesAgo), Now));

    [Theory]
    [InlineData(0.5, "in a moment")]
    [InlineData(2, "in 2 min")]
    [InlineData(58, "in 58 min")]
    [InlineData(170, "in 3 hours")]
    public void Upcoming_times(double minutesAhead, string expected) =>
        Assert.Equal(expected, RelativeTime.FromNow(Now.AddMinutes(minutesAhead), Now));
}

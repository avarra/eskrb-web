using Eskrb.Api.Services;

namespace Eskrb.Api.Tests;

public class EpisodeStatusTests
{
    private static readonly DateOnly Today = new(2026, 11, 21);

    [Theory]
    [InlineData("2026-11-22", "2026-12-30", "upcoming")]
    [InlineData("2026-11-21", "2026-12-30", "active")]   // starts today
    [InlineData("2026-11-01", "2026-11-21", "active")]   // last day is inclusive
    [InlineData("2026-11-01", "2026-11-20", "ended")]
    public void Status_by_dates(string start, string end, string expected)
        => Assert.Equal(expected, EpisodeStatus.Of(DateOnly.Parse(start), DateOnly.Parse(end), Today));

    [Fact]
    public void End_now_is_yesterday_so_the_episode_is_inactive_immediately()
    {
        var start = new DateOnly(2026, 11, 2);
        var end = EpisodeStatus.EndNow(start, Today);
        Assert.Equal(new DateOnly(2026, 11, 20), end);
        Assert.Equal("ended", EpisodeStatus.Of(start, end, Today));
    }

    [Fact]
    public void End_now_never_goes_before_the_start_date()
    {
        var start = Today; // started today: cannot end before it began
        Assert.Equal(start, EpisodeStatus.EndNow(start, Today));
    }
}

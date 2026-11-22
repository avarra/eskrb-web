using Eskrb.Api.Rules;

namespace Eskrb.Api.Tests;

public class BuddyTests
{
    private static readonly DateOnly Today = new(2026, 11, 22);
    private static HashSet<DateOnly> Days(params int[] daysAgo) => daysAgo.Select(d => Today.AddDays(-d)).ToHashSet();
    private static BuddyFacts Facts(HashSet<DateOnly> d, int startDaysAgo = 20) =>
        Buddy.Facts(d, Today, Today.AddDays(-startDaysAgo), Today.AddDays(22), 42);

    [Fact]
    public void Streak_counts_back_from_today_or_yesterday_and_missed_days_back_from_yesterday()
    {
        var today = Facts(Days(0, 1, 2, 3));
        Assert.True(today.CheckedInToday);
        Assert.Equal(4, today.Streak);
        Assert.Equal(0, today.MissedDays);

        var notYet = Facts(Days(1, 2, 3));          // not checked in today: the streak is not broken until the day ends
        Assert.Equal(3, notYet.Streak);

        var gone = Facts(Days(5, 6));               // 3 missed days (1..4 minus... days 1-4 missing)
        Assert.Equal(0, gone.Streak);
        Assert.Equal(4, gone.MissedDays);
    }

    [Fact]
    public void Missed_days_never_count_before_the_episode_started()
        => Assert.Equal(2, Facts(Days(), startDaysAgo: 2).MissedDays);

    [Fact]
    public void Returned_means_checked_in_today_after_a_gap()
    {
        Assert.True(Facts(Days(0, 4, 5)).Returned);
        Assert.False(Facts(Days(0, 1, 2)).Returned);
        Assert.False(Facts(Days(0), startDaysAgo: 3).Returned);   // nothing before the gap
    }

    [Fact]
    public void Moments_follow_the_buddys_situation_and_never_include_a_time_of_day_rule()
    {
        Assert.Contains("not_checked_in", Buddy.Moments(Facts(Days(1)), false));
        Assert.DoesNotContain("missed_days", Buddy.Moments(Facts(Days(1)), false));
        Assert.Contains("missed_days", Buddy.Moments(Facts(Days(5)), false));

        var done = Buddy.Moments(Facts(Days(0, 1, 2)), iCheckedInToday: true);
        Assert.Contains("checked_in", done);
        Assert.Contains("both_checked_in", done);
        Assert.Contains("streak_3", done);
        Assert.DoesNotContain("both_checked_in", Buddy.Moments(Facts(Days(0, 1, 2)), false));
        Assert.DoesNotContain("not_checked_in", done);
    }

    [Fact]
    public void Day_and_halfway_milestones_and_finished_buddies()
    {
        Assert.Contains("day_7", Buddy.Moments(Facts(Days(0), startDaysAgo: 6), false));      // day 7
        Assert.Contains("day_halfway", Buddy.Moments(Facts(Days(0), startDaysAgo: 20), false)); // day 21 of 42
        var finished = Buddy.Facts(Days(10), Today, Today.AddDays(-50), Today.AddDays(-1), 42);
        Assert.Equal(["finished"], Buddy.Moments(finished, true));
    }

    [Fact]
    public void Quiet_hours_are_21_to_08_and_every_catalogue_entry_is_usable()
    {
        Assert.True(Buddy.IsQuietHour(21));
        Assert.True(Buddy.IsQuietHour(3));
        Assert.False(Buddy.IsQuietHour(8));
        Assert.False(Buddy.IsQuietHour(20));
        Assert.Equal(Buddy.Catalogue.Count, Buddy.Catalogue.Select(c => c.Id).Distinct().Count());
        Assert.All(Buddy.Catalogue, c => Assert.False(string.IsNullOrWhiteSpace(c.Text)));
        Assert.Equal(2, Buddy.Catalogue.Count(c => c.Moment == Buddy.Always));
    }
}

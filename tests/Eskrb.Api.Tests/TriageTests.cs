using Eskrb.Api.Rules;

namespace Eskrb.Api.Tests;

public class TriageTests
{
    private static readonly DateOnly Today = new(2026, 11, 22);

    [Theory]
    [InlineData(true, true, true, Triage.NeedsAttention)]
    [InlineData(false, true, true, Triage.MissedCheckIn)]
    [InlineData(false, false, true, Triage.AwaitingPatientReply)]
    [InlineData(false, false, false, Triage.Stable)]
    public void First_matching_column_wins(bool flag, bool missed, bool staffLast, string expected)
        => Assert.Equal(expected, Triage.Column(flag, missed, staffLast));

    [Fact]
    public void Checked_in_today_is_never_missed()
        => Assert.False(Triage.IsMissed(Today, 23, 20, Today.AddDays(-5), true, false));

    [Fact]
    public void Not_yet_checked_in_is_missed_only_after_the_cutoff_or_when_yesterday_was_missed_too()
    {
        Assert.False(Triage.IsMissed(Today, 12, 20, Today.AddDays(-5), false, true));   // morning, yesterday fine
        Assert.True(Triage.IsMissed(Today, 20, 20, Today.AddDays(-5), false, true));    // cutoff passed
        Assert.True(Triage.IsMissed(Today, 9, 20, Today.AddDays(-5), false, false));    // yesterday missed
        Assert.False(Triage.IsMissed(Today, 9, 20, Today, false, false));                // episode starts today
    }
}

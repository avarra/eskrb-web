namespace Eskrb.Api.Services;

/// <summary>An episode is active from its start date through its end date, both inclusive.</summary>
public static class EpisodeStatus
{
    public const string Upcoming = "upcoming";
    public const string Active = "active";
    public const string Ended = "ended";

    public static string Of(DateOnly start, DateOnly end, DateOnly today) =>
        today < start ? Upcoming : today > end ? Ended : Active;

    /// <summary>Last day an "ended now" episode is still active: yesterday, but never before its start.</summary>
    public static DateOnly EndNow(DateOnly start, DateOnly today) =>
        today.AddDays(-1) < start ? start : today.AddDays(-1);
}

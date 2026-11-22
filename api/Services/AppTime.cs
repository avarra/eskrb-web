namespace Eskrb.Api.Services;

/// <summary>"Today" is always Europe/Ljubljana, never server-local or UTC.</summary>
public static class AppTime
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Ljubljana");

    public static DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Zone);

    /// <summary>The UTC instant of the given local (Europe/Ljubljana) day and hour.</summary>
    public static DateTime AtLocal(DateOnly day, int hour) =>
        TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(new TimeOnly(hour, 0)), Zone);

    public static DateOnly Today =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Zone));
}

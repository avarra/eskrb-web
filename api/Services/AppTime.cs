namespace Eskrb.Api.Services;

/// <summary>"Today" is always Europe/Ljubljana, never server-local or UTC.</summary>
public static class AppTime
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Ljubljana");

    public static DateOnly Today =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Zone));
}

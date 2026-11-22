namespace Eskrb.Api.Rules;

/// <summary>Triage column logic. Pure: no I/O, so it is cheap to unit-test.</summary>
public static class Triage
{
    public const string NeedsAttention = "needsAttention";
    public const string MissedCheckIn = "missedCheckIn";
    public const string AwaitingPatientReply = "awaitingPatientReply";
    public const string Stable = "stable";

    /// <summary>
    /// No check-in today and either the cutoff hour has passed, or yesterday was also missed
    /// (only counted when the episode already existed yesterday).
    /// </summary>
    public static bool IsMissed(DateOnly today, int localHour, int cutoffHour, DateOnly start, bool checkedInToday, bool checkedInYesterday)
    {
        if (checkedInToday) return false;
        if (localHour >= cutoffHour) return true;
        return start <= today.AddDays(-1) && !checkedInYesterday;
    }

    /// <summary>First match wins: open flags, then missed check-in, then an unanswered staff message.</summary>
    public static string Column(bool hasOpenFlag, bool missed, bool lastMessageFromStaff) =>
        hasOpenFlag ? NeedsAttention
        : missed ? MissedCheckIn
        : lastMessageFromStaff ? AwaitingPatientReply
        : Stable;
}

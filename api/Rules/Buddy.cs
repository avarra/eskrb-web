namespace Eskrb.Api.Rules;

/// <summary>What a buddy may learn about the other patient: nothing but these (no health data).</summary>
public record BuddyFacts(int DayNumber, int DurationDays, bool CheckedInToday, int Streak, int MissedDays, bool Returned, bool Finished);

/// <summary>A cheer in the catalogue. <see cref="Moment"/> is when it fits; the client decides what to show (incl. time of day).</summary>
public record Cheer(string Id, string Moment, string Label, string Text, string? Starter = null, bool Nudge = false);

/// <summary>Pure buddy logic: facts from check-in dates, which "moments" apply, the cheer catalogue. No I/O.</summary>
public static class Buddy
{
    public const string Always = "always";
    public const string Finished = "finished";

    public static BuddyFacts Facts(IReadOnlySet<DateOnly> checkIns, DateOnly today, DateOnly start, DateOnly end, int durationDays)
    {
        var checkedToday = checkIns.Contains(today);
        var streak = 0;
        for (var d = checkedToday ? today : today.AddDays(-1); d >= start && checkIns.Contains(d); d = d.AddDays(-1)) streak++;

        var missed = 0;
        for (var d = today.AddDays(-1); d >= start && !checkIns.Contains(d); d = d.AddDays(-1)) missed++;

        var yesterday = today.AddDays(-1);
        var returned = checkedToday && yesterday >= start && !checkIns.Contains(yesterday) && checkIns.Any(d => d < yesterday);
        return new BuddyFacts(Math.Max(1, today.DayNumber - start.DayNumber + 1), durationDays, checkedToday, streak, missed, returned, today > end);
    }

    /// <summary>
    /// The moments that apply right now. No time-of-day rule on purpose: the phone decides when to show nudges.
    /// A finished buddy only has "finished".
    /// </summary>
    public static List<string> Moments(BuddyFacts buddy, bool iCheckedInToday)
    {
        if (buddy.Finished) return [Finished];
        var m = new List<string>();
        if (!buddy.CheckedInToday) m.Add("not_checked_in");
        if (!buddy.CheckedInToday && buddy.MissedDays >= 2) m.Add("missed_days");
        if (buddy.CheckedInToday) m.Add("checked_in");
        if (buddy.CheckedInToday && iCheckedInToday) m.Add("both_checked_in");
        if (buddy.Streak is 3 or 7 or 14 or 30) m.Add($"streak_{buddy.Streak}");
        if (buddy.DayNumber is 7 or 14 or 21) m.Add($"day_{buddy.DayNumber}");
        if (buddy.DurationDays >= 4 && buddy.DayNumber == buddy.DurationDays / 2) m.Add("day_halfway");
        if (buddy.Returned) m.Add("returned");
        return m;
    }

    /// <summary>No pushes at night (21:00-08:00 local); the message still shows in the app.</summary>
    public static bool IsQuietHour(int localHour) => localHour >= 21 || localHour < 8;

    // Slovenian, formal "vi". Draft copy: have a native speaker review it.
    public static readonly IReadOnlyList<Cheer> Catalogue =
    [
        new("remind", "not_checked_in", "Spomnite na prijavo", "Kolega misli nate. Ne pozabite na današnjo prijavo.", Nudge: true),
        new("ask", "missed_days", "Vprašajte, kako gre", "Kolega misli nate. Kako vam gre?", Starter: "Kako vam gre?", Nudge: true),
        new("congrats-checkin", "checked_in", "Čestitajte za prijavo", "Bravo za današnjo prijavo."),
        new("both-done", "both_checked_in", "Oba sva danes opravila", "Danes sva oba opravila prijavo."),
        new("streak-3", "streak_3", "Čestitajte za 3 dni zapored", "3 dni zapored, bravo."),
        new("streak-7", "streak_7", "Čestitajte za 7 dni zapored", "7 dni zapored, bravo."),
        new("streak-14", "streak_14", "Čestitajte za 14 dni zapored", "14 dni zapored, bravo."),
        new("streak-30", "streak_30", "Čestitajte za 30 dni zapored", "30 dni zapored, bravo."),
        new("day-7", "day_7", "Že teden dni za nama", "Teden dni okrevanja je za vama."),
        new("day-14", "day_14", "Že dva tedna za nama", "Dva tedna okrevanja sta za vama."),
        new("day-21", "day_21", "Že tri tedne za nama", "Tri tedne okrevanja so za vama."),
        new("day-halfway", "day_halfway", "Že polovica poti za nama", "Polovica okrevanja je za vama."),
        new("welcome-back", "returned", "Pozdravite ob vrnitvi", "Lepo, da ste spet tu."),
        new("congrats-finish", Finished, "Čestitajte", "Čestitke za zaključeno okrevanje."),
        new("fingers-crossed", Always, "Držim pesti", "Držim pesti."),
        new("until-tomorrow", Always, "Do jutri", "Do jutri."),
    ];

    public static Cheer? Find(string? id) => Catalogue.FirstOrDefault(c => c.Id == id);
}

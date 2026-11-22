using System.Text.Json;
using Eskrb.Api.Domain;
using Eskrb.Api.Rules;
using Eskrb.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Eskrb.Api.Data;

/// <summary>
/// The "demo" profile (config Seed:Profile=demo, `./reseed.sh --demo`): ten patients at different stages of a 42-day knee
/// recovery, each with a history UP TO YESTERDAY and NO check-in today (the judges do today's), all paired as buddies with a
/// short conversation. Flags come from the real rules engine; only the answers are made up.
/// </summary>
public static partial class Seeder
{
    private static readonly Guid WingAId = Guid.Parse("00000000-0000-0000-0000-00000000b001");
    private static readonly Guid WingBId = Guid.Parse("00000000-0000-0000-0000-00000000b002");

    /// <param name="Day">Recovery day today (1 = surgery/discharge day).</param>
    private record DemoPatient(string Key, string Episode, int Day, Guid Group);

    private static readonly DemoPatient[] DemoPatients =
    [
        new("janez", "00000000-0000-0000-0000-00000000e005", 3, WingAId),
        new("marija", "00000000-0000-0000-0000-00000000e006", 5, WingAId),
        new("ana", "00000000-0000-0000-0000-00000000e001", 9, WingAId),
        new("marko", "00000000-0000-0000-0000-00000000e002", 12, WingAId),
        new("nina", "00000000-0000-0000-0000-00000000e003", 16, WingBId),
        new("franc", "00000000-0000-0000-0000-00000000e007", 19, WingBId),
        new("ivana", "00000000-0000-0000-0000-00000000e008", 24, WingBId),
        new("luka", "00000000-0000-0000-0000-00000000e004", 28, WingBId),
        new("tone", "00000000-0000-0000-0000-00000000e009", 34, WingAId),
        new("milena", "00000000-0000-0000-0000-00000000e00a", 40, WingAId),
    ];

    /// <summary>(first, second, days ago they were paired) with similar recovery stages.</summary>
    private static readonly (string A, string B, int DaysAgo)[] DemoPairs =
    [
        ("janez", "marija", 2), ("ana", "marko", 6), ("nina", "franc", 10), ("ivana", "luka", 14), ("tone", "milena", 20),
    ];

    private static async Task SeedDemoAsync(EskrbDb db, Services.Media.IMediaStore media, Protocol protocol, Guid doctor)
    {
        var today = AppTime.Today;
        var patients = await db.Users.Where(u => u.Role == UserRole.Patient).ToDictionaryAsync(u => u.Id);
        var byKey = UsersFor(DemoProfile).Where(u => u.Role == UserRole.Patient).ToDictionary(u => u.Key);
        var episodes = new Dictionary<string, Episode>();

        foreach (var p in DemoPatients)
        {
            var id = Guid.Parse(p.Episode);
            var existing = await db.Episodes.FindAsync(id);
            if (existing is not null) { episodes[p.Key] = existing; continue; }

            var start = today.AddDays(-(p.Day - 1));
            var ep = new Episode
            {
                Id = id, PatientId = byKey[p.Key].Id, ProtocolId = protocol.Id, DoctorGroupId = p.Group,
                StartDate = start, EndDate = start.AddDays(protocol.DurationDays),
            };
            db.Episodes.Add(ep);
            episodes[p.Key] = ep;
            DemoHistory(db, media, ep, protocol, p.Key, p.Day - 1, doctor);
        }
        await db.SaveChangesAsync();

        foreach (var (a, b, daysAgo) in DemoPairs)
        {
            if (await db.BuddyPairs.AnyAsync(x => x.PatientAId == episodes[a].PatientId || x.PatientBId == episodes[a].PatientId)) continue;
            var pair = new BuddyPair
            {
                Id = Guid.NewGuid(), EpisodeAId = episodes[a].Id, EpisodeBId = episodes[b].Id,
                PatientAId = episodes[a].PatientId, PatientBId = episodes[b].PatientId,
                CreatedBy = doctor, CreatedAt = AppTime.AtLocal(today.AddDays(-daysAgo), 11),
            };
            db.BuddyPairs.Add(pair);
            DemoConversation(db, pair, a + "+" + b);
        }

        // A few staff notes so the episode page has more than check-ins.
        void Note(string who, string kind, string title, string body) =>
            db.EpisodeNotes.Add(new EpisodeNote
            {
                Id = Guid.NewGuid(), EpisodeId = episodes[who].Id, Kind = kind, Title = title, Body = body,
                CreatedBy = doctor, UpdatedBy = doctor, CreatedAt = AppTime.AtLocal(today.AddDays(-2), 12), UpdatedAt = AppTime.AtLocal(today.AddDays(-2), 12),
            });
        Note("marija", NoteKinds.Medication, "Zdravila", "Protibolečinsko zjutraj in zvečer po obroku. Zdravilo za redčenje krvi vsak dan ob isti uri.");
        Note("marija", NoteKinds.Private, "Opomba", "Rana izcejala včeraj; preveriti fotografijo in poklicati.");
        Note("ana", NoteKinds.Advice, "Vaje", "Vaje za upogibanje kolena trikrat na dan po deset ponovitev.");
        Note("tone", NoteKinds.Advice, "Hoja", "Hodite vsak dan, a se izogibajte dolgemu stanju.");
        await db.SaveChangesAsync();
    }

    /// <summary>History up to YESTERDAY: <paramref name="lastDay"/> = days since the start (today), so the last check-in is lastDay - 1.</summary>
    private static void DemoHistory(EskrbDb db, Services.Media.IMediaStore media, Episode ep, Protocol protocol, string key, int lastDay, Guid doctor)
    {
        var h = new HistoryWriter(db, media, ep, protocol, doctor);
        var photoDays = new[] { 1, 3, 5, 9, 14 };
        byte[]? Photo(int n) { var k = Array.IndexOf(photoDays, n); return k >= 0 && k < DemoPhotos.Value.Count ? DemoPhotos.Value[k] : null; }
        static int Pain(int n, int bump = 0) => Math.Clamp(7 - n / 4 + (n * 5) % 3 - 1 + bump, 1, 9);
        static string Feeling(int n) => (n % 4) switch { 0 => "ok", 1 => "good", 2 => "good", _ => n > 10 ? "great" : "ok" };
        static string[] Wound(int n) => n <= 9 ? ["redness"] : ["none"];
        static string Swelling(int n) => n < 4 ? "slightly_more" : "no";
        var start = ep.StartDate;
        var yesterday = lastDay - 1;

        // n = days since the start; every patient gets one check-in per day up to yesterday, unless the story skips it.
        for (var n = 0; n <= yesterday; n++)
        {
            var date = start.AddDays(n);
            var isYesterday = n == yesterday;
            var reviewed = !isYesterday;   // yesterday's check-in is still waiting for staff
            var photo = Photo(n);
            switch (key)
            {
                case "marija" when isYesterday: // the wound started to leak: an unreviewed attention flag
                    h.CheckIn(date, Pain(n, 1), "bad", "worse", "slightly_more", ["discharge"], "partly", reviewed: false, woundPhoto: photo ?? DemoPhotos.Value.ElementAtOrDefault(1));
                    break;
                case "ivana" when isYesterday: // a new calf complaint: unreviewed attention flag
                    h.CheckIn(date, Pain(n), "ok", "same", "no", ["none"], "all", symptoms: ["calf"], reviewed: false);
                    break;
                case "nina" when n == yesterday:   // nina did not check in yesterday
                    break;
                case "nina" when n == 8:           // a fever that was handled
                    h.CheckIn(date, Pain(n), "bad", "worse", Swelling(n), Wound(n), "all", symptoms: ["fever"], temperature: 38.3, woundPhoto: photo);
                    break;
                case "luka" when n == 17:
                    h.CheckIn(date, Pain(n), "bad", "worse", Swelling(n), ["none"], "partly", symptoms: ["fever"], temperature: 38.1);
                    break;
                case "franc" when n == 10:         // swelling flare that was handled
                    h.CheckIn(date, Pain(n, 1), "ok", "same", "much_more", Wound(n), "partly");
                    break;
                case "franc" when isYesterday:
                    h.CheckIn(date, Pain(n), Feeling(n), "better", "no", ["none"], "all", reviewed: true);
                    break;
                case "ana" when n == 5:            // one skipped day
                case "tone" when n is 20 or 21:
                    break;
                default:
                    h.CheckIn(date, Pain(n), Feeling(n), n % 7 == 0 ? "same" : "better", Swelling(n), Wound(n),
                        key == "milena" && n % 9 == 0 ? "partly" : "all", reviewed: reviewed, woundPhoto: photo);
                    break;
            }
        }

        if (key == "franc")
            h.Message(doctor, HistoryWriter.Past(AppTime.AtLocal(AppTime.Today.AddDays(-1), 18)),
                "Pozdravljeni. Včeraj ste imeli nekoliko več bolečin. Prosim, sporočite nam, kako ste danes.");
    }

    private static void DemoConversation(EskrbDb db, BuddyPair pair, string who)
    {
        var today = AppTime.Today;
        void Say(bool fromA, int daysAgo, int hour, string? text, string? cheer = null, string source = "typed", bool read = true)
        {
            var at = AppTime.AtLocal(today.AddDays(-daysAgo), hour);
            if (at > DateTime.UtcNow) at = DateTime.UtcNow.AddHours(-1);
            db.BuddyMessages.Add(new BuddyMessage
            {
                Id = Guid.NewGuid(), PairId = pair.Id, FromUserId = fromA ? pair.PatientAId : pair.PatientBId,
                Kind = cheer is null ? "text" : "cheer", CheerId = cheer, Text = cheer is null ? text! : Buddy.Find(cheer)!.Text,
                Source = cheer is null ? source : null, CreatedAt = at, ReadAt = read ? at.AddMinutes(25) : null,
            });
        }

        switch (who)
        {
            case "janez+marija":
                Say(false, 1, 15, "Dober dan, Janez. Jaz sem Marija. Tudi meni so pred nekaj dnevi zamenjali koleno.");
                Say(true, 1, 17, "Dober dan, Marija. Veseli me. Prvi dnevi so težki, kajne?");
                Say(false, 1, 18, null, cheer: "fingers-crossed", read: false);
                break;
            case "ana+marko":
                Say(true, 5, 10, "Dober dan, Marko. Kako vam gre z vajami?");
                Say(false, 5, 12, "Dober dan, Ana. Gre počasi, a vsak dan malo več.");
                Say(true, 4, 9, null, cheer: "congrats-checkin");
                Say(false, 3, 15, "Včeraj sem prvič hodil brez ene bergle.", source: "dictated");
                Say(true, 2, 11, "To je odlično. Bravo.");
                Say(false, 1, 12, null, cheer: "fingers-crossed");
                Say(true, 1, 19, "Se jutri slišiva po prijavi.", read: false);
                break;
            case "nina+franc":
                Say(true, 9, 10, "Dober dan, Franc. Sem Nina, v enakem obdobju okrevanja kot vi.");
                Say(false, 9, 14, "Dober dan, Nina. Hvala za sporočilo. Meni noge še otekajo proti večeru.");
                Say(true, 8, 9, "Meni je pomagalo, da noge čez dan dvignem. Vprašajte tudi zdravnico.");
                Say(false, 6, 16, null, cheer: "day-14");
                Say(true, 1, 10, null, cheer: "ask", read: false);
                break;
            case "ivana+luka":
                Say(false, 13, 9, "Dober dan, Ivana. Sem Luka. Kako je z vami po operaciji?");
                Say(true, 13, 11, "Dober dan, Luka. Dobro, vsak dan malo hodim po stanovanju.");
                Say(false, 10, 10, "Meni so svetovali vaje za upogibanje po vsakem obroku.", source: "dictated");
                Say(true, 8, 18, null, cheer: "streak-7");
                Say(false, 4, 12, "Danes sem prvič šel po stopnicah.");
                Say(true, 4, 13, "Čestitam. Bravo.");
                Say(false, 1, 9, null, cheer: "congrats-checkin", read: false);
                break;
            case "tone+milena":
                Say(true, 19, 10, "Dober dan, Milena. Sem Tone, pri meni je tretji teden.");
                Say(false, 19, 12, "Dober dan, Tone. Jaz sem že pri petem tednu, vse je lažje.");
                Say(true, 15, 9, "Kako pa spite? Meni koleno ponoči še moti.");
                Say(false, 15, 11, "Pomagala mi je blazina pod koleni. Po dveh tednih je bilo bolje.");
                Say(true, 7, 16, null, cheer: "day-21");
                Say(false, 3, 10, "Še nekaj dni in sem na cilju.", source: "dictated");
                Say(true, 3, 11, "Čestitam vnaprej. Držim pesti.");
                Say(false, 1, 9, null, cheer: "welcome-back", read: false);
                break;
        }
    }
}

using System.Text.Json;
using Eskrb.Api.Auth;
using Eskrb.Api.Domain;
using Eskrb.Api.Rules;
using Eskrb.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Eskrb.Api.Data;

/// <summary>
/// Idempotent demo seed. Token plaintexts come from config (Seed:Tokens:&lt;key&gt;, via env/user-secrets).
/// If a token isn't configured, one is generated and logged ONCE at creation (only the hash is stored).
/// </summary>
public static partial class Seeder
{
    private record SeedUser(string Key, Guid Id, string Name, UserRole Role);

    private static SeedUser P(string key, int n, string name) => new(key, Guid.Parse($"00000000-0000-0000-0000-{n:x12}"), name, UserRole.Patient);

    private static readonly SeedUser[] Staff =
    [
        new("doctor", Guid.Parse("00000000-0000-0000-0000-0000000000a1"), "Dr. Maja Zupan", UserRole.Staff),
        new("senior", Guid.Parse("00000000-0000-0000-0000-0000000000a2"), "Dr. Peter Zorko", UserRole.Staff),
        new("admin", Guid.Parse("00000000-0000-0000-0000-0000000000a0"), "Admin", UserRole.Admin),
    ];

    /// <summary>dev (default): 4 patients incl. today's check-ins, for development. demo: 10 patients with history but NO check-in today, all paired as buddies.</summary>
    private static SeedUser[] UsersFor(string profile) => profile == "demo"
        ? [P("ana", 1, "Ana Novak"), P("marko", 2, "Marko Horvat"), P("nina", 3, "Nina Kovač"), P("luka", 4, "Luka Krajnc"),
           P("janez", 5, "Janez Kovačič"), P("marija", 6, "Marija Kranjc"), P("franc", 7, "Franc Zupančič"),
           P("ivana", 8, "Ivana Potočnik"), P("tone", 9, "Tone Mlakar"), P("milena", 10, "Milena Vidmar"), .. Staff]
        : [P("ana", 1, "Ana Novak"), P("marko", 2, "Marko Horvat"), P("nina", 3, "Nina Kovač"), P("luka", 4, "Luka Krajnc"), .. Staff];

    public const string DevProfile = "dev";
    public const string DemoProfile = "demo";

    private const string SeedLabel = "seed";

    public static async Task SeedAsync(EskrbDb db, IConfiguration config, ILogger logger, Services.Media.IMediaStore media)
    {
        await db.Database.EnsureCreatedAsync();

        await AddMissingTablesAsync(db, logger);

        var profile = config["Seed:Profile"] is { Length: > 0 } cfg ? cfg : DevProfile;
        var users = UsersFor(profile);
        logger.LogInformation("Seeding profile '{Profile}'", profile);

        foreach (var s in users)
        {
            if (!await db.Users.AnyAsync(u => u.Id == s.Id))
                db.Users.Add(new User { Id = s.Id, Name = s.Name, Role = s.Role });
        }
        await db.SaveChangesAsync();

        foreach (var s in users)
        {
            var configured = config[$"Seed:Tokens:{s.Key}"];
            if (!string.IsNullOrWhiteSpace(configured))
            {
                var hash = TokenHasher.Hash(configured);
                if (!await db.AuthTokens.AnyAsync(t => t.TokenHash == hash))
                    db.AuthTokens.Add(NewToken(s.Id, hash));
            }
            else if (!await db.AuthTokens.AnyAsync(t => t.UserId == s.Id && t.Label == SeedLabel))
            {
                var generated = TokenHasher.Generate();
                db.AuthTokens.Add(NewToken(s.Id, TokenHasher.Hash(generated)));
                logger.LogWarning(
                    "No Seed:Tokens:{Key} configured; generated token for {Name} (shown once): {Token}",
                    s.Key, s.Name, generated);
            }
        }
        await db.SaveChangesAsync();

        await SeedProtocolsAsync(db);
        await SeedGroupsAndEpisodesAsync(db, media, users, profile);

        // ./reseed.sh exports the registered push devices before wiping; bring them back (the seeded users keep their ids).
        var dataSource = db.Database.GetDbConnection().DataSource;
        if (!string.IsNullOrEmpty(dataSource))
            await ImportPreservedPushDevicesAsync(db, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dataSource))!, PreservedPushDevicesFile), logger);
    }

    public const string PreservedPushDevicesFile = "preserved-push-devices.json";

    /// <summary>
    /// Re-creates push devices exported by ./reseed.sh (JSON rows Id, UserId, ExpoToken, Platform, DeviceId, CreatedAt, UpdatedAt),
    /// so a reseed does not unlink phones from their users. Skips rows whose user no longer exists or whose token is already
    /// registered, then renames the file to <c>.imported</c>. A broken file is left in place and only logged.
    /// </summary>
    public static async Task<int> ImportPreservedPushDevicesAsync(EskrbDb db, string path, ILogger logger)
    {
        if (!File.Exists(path)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var users = (await db.Users.Select(u => u.Id).ToListAsync()).ToHashSet();
            var known = (await db.PushDevices.Select(d => d.ExpoToken).ToListAsync()).ToHashSet();
            var restored = 0;
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                var token = row.GetProperty("ExpoToken").GetString()!;
                var userId = Guid.Parse(row.GetProperty("UserId").GetString()!);
                if (!users.Contains(userId) || !known.Add(token)) continue;
                DateTime When(string name) => DateTime.SpecifyKind(DateTime.Parse(row.GetProperty(name).GetString()!, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);
                db.PushDevices.Add(new PushDevice
                {
                    Id = Guid.Parse(row.GetProperty("Id").GetString()!), UserId = userId, ExpoToken = token,
                    Platform = row.GetProperty("Platform").GetString()!,
                    DeviceId = row.TryGetProperty("DeviceId", out var dev) && dev.ValueKind == JsonValueKind.String ? dev.GetString() : null,
                    CreatedAt = When("CreatedAt"), UpdatedAt = When("UpdatedAt"),
                });
                restored++;
            }
            await db.SaveChangesAsync();
            File.Move(path, path + ".imported", overwrite: true);
            logger.LogInformation("Restored {Count} push device(s) preserved by reseed", restored);
            return restored;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not import {File}; leaving it in place", Path.GetFileName(path));
            return 0;
        }
    }

    /// <summary>
    /// EnsureCreated never adds tables to an existing file, so create any table the model has but the DB lacks
    /// (plus that table's indexes). Additive only: existing data is untouched. It does NOT add columns to
    /// existing tables; after such a change run ./reseed.sh (see APITODO: migrations).
    /// </summary>
    private static async Task AddMissingTablesAsync(EskrbDb db, ILogger logger)
    {
        var existing = (await db.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table'")
            .ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in System.Text.RegularExpressions.Regex.Split(db.Database.GenerateCreateScript(), @";\s*\r?\n"))
        {
            var stmt = raw.Trim();
            if (stmt.Length == 0) continue;

            var table = System.Text.RegularExpressions.Regex.Match(stmt, "^CREATE TABLE \"(\\w+)\"");
            var index = System.Text.RegularExpressions.Regex.Match(stmt, "^CREATE (?:UNIQUE )?INDEX \"[^\"]+\" ON \"(\\w+)\"");
#pragma warning disable EF1002 // statements come from the EF model, not user input
            if (table.Success && !existing.Contains(table.Groups[1].Value))
            {
                await db.Database.ExecuteSqlRawAsync(stmt);
                created.Add(table.Groups[1].Value);
                logger.LogInformation("Created missing table {Table}", table.Groups[1].Value);
            }
            else if (index.Success && created.Contains(index.Groups[1].Value))
            {
                await db.Database.ExecuteSqlRawAsync(stmt);
            }
#pragma warning restore EF1002
        }
    }

    /// <summary>Protocols are config: overwritten from the seed files on every start.</summary>
    private static async Task SeedProtocolsAsync(EskrbDb db)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Data", "Seed");
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            var p = JsonSerializer.Deserialize<Protocol>(
                await File.ReadAllTextAsync(file), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var existing = await db.Protocols.FindAsync(p.Id);
            if (existing is null) db.Protocols.Add(p);
            else
            {
                existing.Name = p.Name;
                existing.DurationDays = p.DurationDays;
                existing.Questions = p.Questions;
                existing.Rules = p.Rules;
            }
        }
        await db.SaveChangesAsync();
    }

    private static readonly Guid WingA = Guid.Parse("00000000-0000-0000-0000-00000000b001");
    private static readonly Guid WingB = Guid.Parse("00000000-0000-0000-0000-00000000b002");

    /// <summary>Groups, memberships and episodes are only created if missing, never overwritten.</summary>
    private static async Task SeedGroupsAndEpisodesAsync(EskrbDb db, Services.Media.IMediaStore media, SeedUser[] users, string profile)
    {
        var doctor = users.Single(u => u.Key == "doctor").Id;

        async Task Group(Guid id, string name)
        {
            if (!await db.DoctorGroups.AnyAsync(g => g.Id == id))
            {
                db.DoctorGroups.Add(new DoctorGroup { Id = id, Name = name });
                db.DoctorGroupMembers.Add(new DoctorGroupMember
                {
                    Id = Guid.NewGuid(), GroupId = id, UserId = doctor, Level = 1, Title = "Doctor",
                });
            }
        }
        await Group(WingA, "Orthopedics - Wing A");
        await Group(WingB, "Orthopedics - Wing B");
        // A second, more senior doctor (level 2) in both wings, so escalation has someone to escalate to.
        var senior = users.Single(u => u.Key == "senior").Id;
        foreach (var g in new[] { WingA, WingB })
            if (!await db.DoctorGroupMembers.AnyAsync(m => m.GroupId == g && m.UserId == senior))
                db.DoctorGroupMembers.Add(new DoctorGroupMember { Id = Guid.NewGuid(), GroupId = g, UserId = senior, Level = 2, Title = "Senior doctor" });
        await db.SaveChangesAsync();

        var today = AppTime.Today;
        var protocol = await db.Protocols.FindAsync("knee-replacement");
        if (protocol is null) return;
        if (profile == DemoProfile)
        {
            await SeedDemoAsync(db, media, protocol, doctor);
            return;
        }

        // (patient key, episode id, group, current day number)
        var episodes = new (string Key, string Id, Guid Group, int Day)[]
        {
            ("ana", "00000000-0000-0000-0000-00000000e001", WingA, 20),
            ("marko", "00000000-0000-0000-0000-00000000e002", WingA, 10),
            ("nina", "00000000-0000-0000-0000-00000000e003", WingB, 30),
            ("luka", "00000000-0000-0000-0000-00000000e004", WingB, 15),
        };
        var createdNow = new HashSet<string>();
        foreach (var e in episodes)
        {
            var id = Guid.Parse(e.Id);
            if (await db.Episodes.AnyAsync(x => x.Id == id)) continue;
            var start = today.AddDays(-(e.Day - 1));
            var episode = new Episode
            {
                Id = id,
                PatientId = users.Single(u => u.Key == e.Key).Id,
                ProtocolId = protocol.Id,
                DoctorGroupId = e.Group,
                StartDate = start,
                EndDate = start.AddDays(protocol.DurationDays),
            };
            db.Episodes.Add(episode);
            SeedHistory(db, media, episode, protocol, e.Key, doctor);
            createdNow.Add(e.Key);
        }
        await db.SaveChangesAsync();

        // Demo buddy pair (ana + luka) with a short conversation on a fresh seed. marko and nina stay free so staff can pair them live.
        if (createdNow.Contains("ana") && createdNow.Contains("luka"))
        {
            var anaEp = await db.Episodes.FindAsync(Guid.Parse("00000000-0000-0000-0000-00000000e001"));
            var lukaEp = await db.Episodes.FindAsync(Guid.Parse("00000000-0000-0000-0000-00000000e004"));
            var pair = new BuddyPair
            {
                Id = Guid.NewGuid(), EpisodeAId = anaEp!.Id, EpisodeBId = lukaEp!.Id, PatientAId = anaEp.PatientId, PatientBId = lukaEp.PatientId,
                CreatedBy = doctor, CreatedAt = AppTime.AtLocal(today.AddDays(-5), 11),
            };
            db.BuddyPairs.Add(pair);

            var ana = anaEp.PatientId;
            var luka = lukaEp.PatientId;
            void Say(Guid from, int daysAgo, int hour, string text, string? source = "typed", string? cheer = null, bool read = true)
            {
                var at = AppTime.AtLocal(today.AddDays(-daysAgo), hour);
                if (at > DateTime.UtcNow) at = DateTime.UtcNow.AddMinutes(-30);
                db.BuddyMessages.Add(new BuddyMessage
                {
                    Id = Guid.NewGuid(), PairId = pair.Id, FromUserId = from, Kind = cheer is null ? "text" : "cheer", CheerId = cheer,
                    Text = text, Source = cheer is null ? source : null, CreatedAt = at, ReadAt = read ? at.AddMinutes(20) : null,
                });
            }
            Say(luka, 4, 10, "Dober dan, Ana. Sem Luka, tudi jaz sem po operaciji kolena. Kako vam gre?");
            Say(ana, 4, 11, "Dober dan, Luka. Gre počasi, a vsak dan malo bolje. Kako je z vami?");
            Say(luka, 3, 9, "Bravo za današnjo prijavo.", cheer: "congrats-checkin");
            Say(ana, 3, 15, "Hvala! Danes sem prvič prehodila deset minut z berglami.", source: "dictated");
            Say(luka, 2, 10, "To je odlično. Meni so svetovali redne vaje za upogibanje.");
            Say(ana, 2, 18, "Držim pesti.", cheer: "fingers-crossed");
            Say(luka, 1, 12, "Ali ste že poskusili stopnice?");
            Say(ana, 1, 13, "Še ne, jutri bomo poskusili s fizioterapevtom.");
            Say(luka, 0, 9, "Danes sva oba opravila prijavo.", cheer: "both-done", read: false);
            Say(luka, 0, 10, "Kako je šlo z vajami danes?", read: false);
            await db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Demo history for a newly created episode, so each triage column has someone in it:
    /// ana = stable, marko = urgent unresolved flags, nina = stopped checking in, luka = waiting for a reply to a staff message.
    /// Flags come from the real rules engine; only the answers are made up.
    /// </summary>
    /// <summary>Writes seeded history (check-ins with flags and photos, messages) for one episode.</summary>
    private sealed class HistoryWriter(EskrbDb db, Services.Media.IMediaStore media, Episode ep, Protocol protocol, Guid doctor)
    {
        // Answers to the conditional follow-up questions, used when their condition is met (keeps the seed consistent with showIf).
        private static readonly Dictionary<string, JsonElement> FollowUps = new()
        {
            ["feelingCause"] = J("pain"), ["worseningCause"] = J("pain"), ["painWhen"] = J("walking"), ["swellingMobility"] = J("somewhat"),
            ["woundWhyNot"] = J("covered"), ["exercisesWhy"] = J("fatigue"), ["medsWhy"] = J("forgot"), ["calfLeg"] = J("operated"),
        };

        public static DateTime Past(DateTime utc) => utc > DateTime.UtcNow ? DateTime.UtcNow.AddMinutes(-1) : utc;

        string Label(string question, string option) =>
            protocol.Questions.FirstOrDefault(q => q.Id == question)?.Labels?.GetValueOrDefault(option) ?? option;

        public void CheckIn(DateOnly date, int pain, string feeling = "good", string progress = "better", string swelling = "no",
            string[]? wound = null, string exercises = "all", string meds = "yes", string[]? symptoms = null, double? temperature = null,
            bool reviewed = true, byte[]? woundPhoto = null)
        {
            var answers = new Dictionary<string, JsonElement>
            {
                ["feeling"] = J(feeling), ["progress"] = J(progress), ["pain"] = J(pain), ["swelling"] = J(swelling),
                ["wound"] = J(wound ?? ["none"]), ["exercises"] = J(exercises), ["meds"] = J(meds), ["symptoms"] = J(symptoms ?? ["none"]),
            };
            if (temperature is { } t) answers["temperature"] = J(t);
            foreach (var q in protocol.Questions)
                if (FollowUps.TryGetValue(q.Id, out var v) && Conditions.IsMet(q.ShowIf, answers)) answers[q.Id] = v;

            var at = Past(AppTime.AtLocal(date, 9));
            if (woundPhoto is not null)
            {
                // Demo wound photo (api/Data/Seed/photos) stored like any check-in photo: a photo Log + the answer URL.
                using var jpg = new MemoryStream(woundPhoto);
                var logId = Guid.NewGuid();
                db.Logs.Add(new Log
                {
                    Id = logId, EpisodeId = ep.Id, Type = "photo", MediaContentType = "image/jpeg", AuthoredBy = ep.PatientId, CreatedAt = at,
                    MediaKey = media.SaveAsync(jpg, ".jpg", CancellationToken.None).GetAwaiter().GetResult(),
                });
                answers["woundPhoto"] = J($"/media/{logId}");
            }
            var c = new CheckIn
            {
                Id = Guid.NewGuid(), EpisodeId = ep.Id, Date = date, Answers = answers, AuthoredBy = ep.PatientId, CreatedAt = at,
                ReviewedAt = reviewed ? at.AddHours(3) : null, ReviewedBy = reviewed ? doctor : null,
            };
            db.CheckIns.Add(c);
            foreach (var h in RulesEngine.Evaluate(answers, protocol.Rules))
                db.Flags.Add(new Flag
                {
                    Id = Guid.NewGuid(), EpisodeId = ep.Id, SourceType = "checkin", SourceId = c.Id, RuleId = h.RuleId,
                    Severity = h.Severity, CreatedAt = at, ResolvedAt = reviewed ? at.AddHours(3) : null,
                    Quote = $"{h.Field}: " + (h.Answer.ValueKind == JsonValueKind.Array
                        ? string.Join(", ", h.Answer.EnumerateArray().Select(a => Label(h.Field, a.GetString()!)))
                        : h.Answer.ValueKind == JsonValueKind.String ? Label(h.Field, h.Answer.GetString()!) : h.Answer.ToString()),
                });
        }
        public void Message(Guid from, DateTime at, string body) =>
            db.Messages.Add(new Message { Id = Guid.NewGuid(), EpisodeId = ep.Id, FromUserId = from, Body = body, CreatedAt = at });
    }

    private static void SeedHistory(EskrbDb db, Services.Media.IMediaStore media, Episode ep, Protocol protocol, string key, Guid doctor)
    {
        var today = AppTime.Today;
        var days = today.DayNumber - ep.StartDate.DayNumber;   // 0 = first day

        var h = new HistoryWriter(db, media, ep, protocol, doctor);
        void CheckIn(DateOnly date, int pain, string feeling = "good", string progress = "better", string swelling = "no",
            string[]? wound = null, string exercises = "all", string meds = "yes", string[]? symptoms = null, double? temperature = null,
            bool reviewed = true, byte[]? woundPhoto = null) =>
            h.CheckIn(date, pain, feeling, progress, swelling, wound, exercises, meds, symptoms, temperature, reviewed, woundPhoto);
        void Message(Guid from, DateTime at, string body) => h.Message(from, at, body);

        // Mild day-to-day variation so the demo charts look like real readings (all below every rule threshold).
        static string Feeling(int n) => (n % 4) switch { 0 => "ok", 1 => "good", 2 => "good", _ => n > 10 ? "great" : "ok" };
        // Wound photos (freshest -> healed) go on a few early check-in days, so the photo timeline has a story to show.
        var photoDays = new[] { 1, 3, 5, 9, 14 };
        byte[]? PhotoFor(int n) { var k = Array.IndexOf(photoDays, n); return k >= 0 && k < DemoPhotos.Value.Count ? DemoPhotos.Value[k] : null; }
        // A mild redness is normal for the first days; the photo is only asked while the wound shows a change.
        string[] WoundAt(int n) => n <= 9 ? ["redness"] : ["none"];
        static DateTime Past(DateTime utc) => HistoryWriter.Past(utc);
        var first = ep.StartDate.AddDays(1);
        switch (key)
        {
            case "ana": // recovering well, everything reviewed; a short gap a week ago, then a 5-day streak up to today
                for (var d = first; d <= today; d = d.AddDays(1))
                {
                    if (d >= today.AddDays(-7) && d <= today.AddDays(-5)) continue;
                    var n = d.DayNumber - ep.StartDate.DayNumber;
                    CheckIn(d, Math.Clamp(6 - n / 4 + (n * 5) % 3 - 1, 1, 7), Feeling(n), n % 5 == 0 ? "same" : "better", n < 4 ? "slightly_more" : "no",
                        WoundAt(n), n % 6 == 0 ? "partly" : "all", reviewed: d < today, woundPhoto: PhotoFor(n));
                }
                break;
            case "marko": // getting worse: more pain, swelling and a new calf problem today (flags), plus a worrying note
                for (var d = first; d < today; d = d.AddDays(1))
                {
                    var n = d.DayNumber - ep.StartDate.DayNumber;
                    CheckIn(d, 4 + n / 3 + (n % 2), n > 6 ? "ok" : "good", n > 5 ? "same" : "better", n > 5 ? "slightly_more" : "no",
                        n <= 9 ? ["redness"] : ["none"], n > 6 ? "partly" : "all", woundPhoto: n <= 5 ? PhotoFor(n) : null);
                }
                // today the wound looks worse again: reuse an early-stage picture
                CheckIn(today, 8, "bad", "worse", "much_more", ["redness"], "partly", symptoms: ["calf"], reviewed: false,
                    woundPhoto: DemoPhotos.Value.Count > 1 ? DemoPhotos.Value[1] : null);
                var note = "Danes me je nenadoma zmanjkalo sape in pri prsih me stiska.";
                var logAt = Past(AppTime.AtLocal(today, 10));
                var log = new Log
                {
                    Id = Guid.NewGuid(), EpisodeId = ep.Id, Type = "text", Source = "typed", Text = note, AuthoredBy = ep.PatientId,
                    CreatedAt = logAt, Extracted = new() { ["symptoms"] = J(new[] { "breathing_chest" }) },
                };
                db.Logs.Add(log);
                db.Flags.Add(new Flag
                {
                    Id = Guid.NewGuid(), EpisodeId = ep.Id, SourceType = "log", SourceId = log.Id, RuleId = "breathing",
                    Severity = "urgent", Quote = "zmanjkalo sape", CreatedAt = logAt,
                });
                break;
            case "nina": // stopped checking in 3 days ago
                for (var d = first; d <= today.AddDays(-3); d = d.AddDays(1))
                {
                    var n = d.DayNumber - ep.StartDate.DayNumber;
                    CheckIn(d, Math.Clamp(3 - n / 8 + (d.DayNumber % 3 - 1), 0, 6), Feeling(n), "better", n < 4 ? "slightly_more" : "no",
                        WoundAt(n), "all", woundPhoto: PhotoFor(n));
                }
                break;
            case "luka": // a fever was handled; staff asked for a follow-up and the patient has not answered
                for (var d = first; d <= today; d = d.AddDays(1))
                {
                    var n = d.DayNumber - ep.StartDate.DayNumber;
                    var fever = d == today.AddDays(-1);
                    CheckIn(d, Math.Clamp(5 - n / 4 + (n % 2), 1, 7), fever ? "bad" : Feeling(n), fever ? "worse" : "better", n < 4 ? "slightly_more" : "no",
                        WoundAt(n), "all", symptoms: fever ? ["fever"] : null, temperature: fever ? 38.2 : null, woundPhoto: PhotoFor(n));
                }
                Message(doctor, Past(AppTime.AtLocal(today, 8)), "Včeraj ste imeli povišano temperaturo. Prosim, izmerite jo še zvečer in nam sporočite.");
                break;
        }
    }

    private static JsonElement J<T>(T value) => JsonSerializer.SerializeToElement(value);

    /// <summary>Demo wound photos shipped with the app (api/Data/Seed/photos/knee_dayN.jpg), ordered by N (freshest first).</summary>
    private static readonly Lazy<List<byte[]>> DemoPhotos = new(() =>
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Data", "Seed", "photos");
        if (!Directory.Exists(dir)) return [];
        return Directory.GetFiles(dir, "*.jpg")
            .OrderBy(f => int.TryParse(System.Text.RegularExpressions.Regex.Match(Path.GetFileName(f), @"\d+").Value, out var n) ? n : 0)
            .Select(File.ReadAllBytes).ToList();
    });

    private static AuthToken NewToken(Guid userId, string hash) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        TokenHash = hash,
        CreatedAt = DateTime.UtcNow,
        Label = SeedLabel,
    };
}

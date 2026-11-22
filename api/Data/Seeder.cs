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
public static class Seeder
{
    private record SeedUser(string Key, Guid Id, string Name, UserRole Role);

    private static readonly SeedUser[] Users =
    [
        new("ana", Guid.Parse("00000000-0000-0000-0000-000000000001"), "Ana Novak", UserRole.Patient),
        new("marko", Guid.Parse("00000000-0000-0000-0000-000000000002"), "Marko Horvat", UserRole.Patient),
        new("nina", Guid.Parse("00000000-0000-0000-0000-000000000003"), "Nina Kovač", UserRole.Patient),
        new("luka", Guid.Parse("00000000-0000-0000-0000-000000000004"), "Luka Krajnc", UserRole.Patient),
        new("doctor", Guid.Parse("00000000-0000-0000-0000-0000000000a1"), "Dr. Maja Zupan", UserRole.Staff),
        new("senior", Guid.Parse("00000000-0000-0000-0000-0000000000a2"), "Dr. Peter Zorko", UserRole.Staff),
        new("admin", Guid.Parse("00000000-0000-0000-0000-0000000000a0"), "Admin", UserRole.Admin),
    ];

    private const string SeedLabel = "seed";

    public static async Task SeedAsync(EskrbDb db, IConfiguration config, ILogger logger)
    {
        await db.Database.EnsureCreatedAsync();

        await AddMissingTablesAsync(db, logger);

        foreach (var s in Users)
        {
            if (!await db.Users.AnyAsync(u => u.Id == s.Id))
                db.Users.Add(new User { Id = s.Id, Name = s.Name, Role = s.Role });
        }
        await db.SaveChangesAsync();

        foreach (var s in Users)
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
        await SeedGroupsAndEpisodesAsync(db);
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
    private static async Task SeedGroupsAndEpisodesAsync(EskrbDb db)
    {
        var doctor = Users.Single(u => u.Key == "doctor").Id;

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
        var senior = Users.Single(u => u.Key == "senior").Id;
        foreach (var g in new[] { WingA, WingB })
            if (!await db.DoctorGroupMembers.AnyAsync(m => m.GroupId == g && m.UserId == senior))
                db.DoctorGroupMembers.Add(new DoctorGroupMember { Id = Guid.NewGuid(), GroupId = g, UserId = senior, Level = 2, Title = "Senior doctor" });
        await db.SaveChangesAsync();

        var today = AppTime.Today;
        var protocol = await db.Protocols.FindAsync("knee-replacement");
        if (protocol is null) return;

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
                PatientId = Users.Single(u => u.Key == e.Key).Id,
                ProtocolId = protocol.Id,
                DoctorGroupId = e.Group,
                StartDate = start,
                EndDate = start.AddDays(protocol.DurationDays),
            };
            db.Episodes.Add(episode);
            SeedHistory(db, episode, protocol, e.Key, doctor);
            createdNow.Add(e.Key);
        }
        await db.SaveChangesAsync();

        // Demo buddy pair (luka + nina) on a fresh seed; ana and marko stay free so staff can pair them live.
        if (createdNow.Contains("luka") && await db.Episodes.FindAsync(Guid.Parse("00000000-0000-0000-0000-00000000e003")) is { } ninaEp)
        {
            var lukaEp = await db.Episodes.FindAsync(Guid.Parse("00000000-0000-0000-0000-00000000e004"));
            var pair = new BuddyPair
            {
                Id = Guid.NewGuid(), EpisodeAId = lukaEp!.Id, EpisodeBId = ninaEp.Id, PatientAId = lukaEp.PatientId, PatientBId = ninaEp.PatientId,
                CreatedBy = doctor, CreatedAt = DateTime.UtcNow.AddDays(-2),
            };
            db.BuddyPairs.Add(pair);
            db.BuddyMessages.Add(new BuddyMessage { Id = Guid.NewGuid(), PairId = pair.Id, FromUserId = lukaEp.PatientId, Kind = "cheer", CheerId = "fingers-crossed", Text = "Držim pesti.", CreatedAt = DateTime.UtcNow.AddDays(-1) });
            db.BuddyMessages.Add(new BuddyMessage { Id = Guid.NewGuid(), PairId = pair.Id, FromUserId = lukaEp.PatientId, Kind = "text", Source = "typed", Text = "Dober dan, kako vam gre z vajami?", CreatedAt = DateTime.UtcNow.AddHours(-3) });
            await db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Demo history for a newly created episode, so each triage column has someone in it:
    /// ana = stable, marko = urgent unresolved flags, nina = stopped checking in, luka = waiting for a reply to a staff message.
    /// Flags come from the real rules engine; only the answers are made up.
    /// </summary>
    private static void SeedHistory(EskrbDb db, Episode ep, Protocol protocol, string key, Guid doctor)
    {
        var today = AppTime.Today;
        var days = today.DayNumber - ep.StartDate.DayNumber;   // 0 = first day

        void CheckIn(DateOnly date, int pain, double temp, string wound, bool calf, int wellbeing, bool reviewed, bool breathing = false)
        {
            var answers = new Dictionary<string, JsonElement>
            {
                ["pain"] = JsonSerializer.SerializeToElement(pain),
                ["temperature"] = JsonSerializer.SerializeToElement(temp),
                ["wound"] = JsonSerializer.SerializeToElement(wound),
                ["calfPain"] = JsonSerializer.SerializeToElement(calf),
                ["breathing"] = JsonSerializer.SerializeToElement(breathing),
                ["walking"] = JsonSerializer.SerializeToElement(true),
                ["wellbeing"] = JsonSerializer.SerializeToElement(wellbeing),
            };
            var at = Past(AppTime.AtLocal(date, 9));
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
                    Severity = h.Severity, Quote = $"{h.Field}: {h.Answer.ToString().ToLowerInvariant()}", CreatedAt = at,
                    ResolvedAt = reviewed ? at.AddHours(3) : null,
                });
        }
        void Message(Guid from, DateTime at, string body) =>
            db.Messages.Add(new Message { Id = Guid.NewGuid(), EpisodeId = ep.Id, FromUserId = from, Body = body, CreatedAt = at });

        static DateTime Past(DateTime utc) => utc > DateTime.UtcNow ? DateTime.UtcNow.AddMinutes(-1) : utc;
        var first = ep.StartDate.AddDays(1);
        switch (key)
        {
            case "ana": // recovering well, everything reviewed
                for (var d = first; d <= today; d = d.AddDays(1))
                    CheckIn(d, Math.Max(1, 6 - (d.DayNumber - ep.StartDate.DayNumber) / 4), 36.6, "dry", false, 4, reviewed: d < today);
                break;
            case "marko": // getting worse: calf pain today (urgent), a note about it
                for (var d = first; d < today; d = d.AddDays(1))
                    CheckIn(d, 4 + (d.DayNumber - ep.StartDate.DayNumber) / 3, 36.9, "dry", false, 3, reviewed: true);
                CheckIn(today, 8, 37.4, "red", true, 2, reviewed: false);
                var note = "Danes me zelo boli leva meča in koleno je bolj otekla.";
                var logAt = Past(AppTime.AtLocal(today, 10));
                var log = new Log
                {
                    Id = Guid.NewGuid(), EpisodeId = ep.Id, Type = "text", Source = "typed", Text = note, AuthoredBy = ep.PatientId,
                    CreatedAt = logAt, Extracted = new() { ["calfPain"] = JsonSerializer.SerializeToElement(true) },
                };
                db.Logs.Add(log);
                db.Flags.Add(new Flag
                {
                    Id = Guid.NewGuid(), EpisodeId = ep.Id, SourceType = "log", SourceId = log.Id, RuleId = "calf",
                    Severity = "urgent", Quote = "zelo boli leva meča", CreatedAt = logAt,
                });
                break;
            case "nina": // stopped checking in 3 days ago
                for (var d = first; d <= today.AddDays(-3); d = d.AddDays(1))
                    CheckIn(d, 2, 36.6, "dry", false, 4, reviewed: true);
                break;
            case "luka": // a fever was handled; staff asked for a follow-up and the patient has not answered
                for (var d = first; d <= today; d = d.AddDays(1))
                {
                    var fever = d == today.AddDays(-1);
                    CheckIn(d, 3, fever ? 38.2 : 36.8, "dry", false, 4, reviewed: true);
                }
                Message(doctor, Past(AppTime.AtLocal(today, 8)), "Včeraj ste imeli povišano temperaturo. Prosim, izmerite jo še zvečer in nam sporočite.");
                break;
        }
    }

    private static AuthToken NewToken(Guid userId, string hash) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        TokenHash = hash,
        CreatedAt = DateTime.UtcNow,
        Label = SeedLabel,
    };
}

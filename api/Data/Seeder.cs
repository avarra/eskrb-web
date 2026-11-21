using System.Text.Json;
using Eskrb.Api.Auth;
using Eskrb.Api.Domain;
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
        new("doctor", Guid.Parse("00000000-0000-0000-0000-0000000000a1"), "Dr. Maja Zupan", UserRole.Staff),
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
        };
        foreach (var e in episodes)
        {
            var id = Guid.Parse(e.Id);
            if (await db.Episodes.AnyAsync(x => x.Id == id)) continue;
            var start = today.AddDays(-(e.Day - 1));
            db.Episodes.Add(new Episode
            {
                Id = id,
                PatientId = Users.Single(u => u.Key == e.Key).Id,
                ProtocolId = protocol.Id,
                DoctorGroupId = e.Group,
                StartDate = start,
                EndDate = start.AddDays(protocol.DurationDays),
            });
        }
        await db.SaveChangesAsync();
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

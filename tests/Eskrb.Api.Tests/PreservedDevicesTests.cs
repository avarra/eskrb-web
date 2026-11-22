using System.Text.Json;
using Eskrb.Api.Data;
using Eskrb.Api.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eskrb.Api.Tests;

public class PreservedDevicesTests : IDisposable
{
    private readonly SqliteConnection conn = new("Data Source=:memory:");
    private readonly EskrbDb db;
    private readonly string dir = Path.Combine(Path.GetTempPath(), "eskrb-preserve-" + Guid.NewGuid().ToString("N"));
    private static readonly Guid Ana = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public PreservedDevicesTests()
    {
        Directory.CreateDirectory(dir);
        conn.Open();
        db = new EskrbDb(new DbContextOptionsBuilder<EskrbDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        db.Users.Add(new User { Id = Ana, Name = "Ana Novak", Role = UserRole.Patient });
        db.SaveChanges();
    }

    public void Dispose() { db.Dispose(); conn.Dispose(); try { Directory.Delete(dir, true); } catch { /* best effort */ } }

    private string Write(object rows)
    {
        var path = Path.Combine(dir, "preserved-push-devices.json");
        File.WriteAllText(path, JsonSerializer.Serialize(rows));
        return path;
    }

    // the exact shape `sqlite3 -json` produces (uppercase GUIDs, "yyyy-MM-dd HH:mm:ss.fffffff" dates, null DeviceId)
    private static object Row(string user, string token) => new Dictionary<string, object?>
    {
        ["Id"] = Guid.NewGuid().ToString().ToUpperInvariant(), ["UserId"] = user, ["ExpoToken"] = token, ["Platform"] = "ios",
        ["DeviceId"] = null, ["CreatedAt"] = "2026-11-22 10:18:22.2327575", ["UpdatedAt"] = "2026-11-22 10:18:22.2327575",
    };

    [Fact]
    public async Task Restores_devices_for_known_users_skips_unknown_users_and_duplicates_then_marks_the_file_done()
    {
        var path = Write(new[]
        {
            Row(Ana.ToString(), "ExponentPushToken[keep-me]"),
            Row(Ana.ToString(), "ExponentPushToken[keep-me]"),                                   // duplicate token
            Row("00000000-0000-0000-0000-0000000000ff", "ExponentPushToken[orphan]"),            // user is gone
        });

        var n = await Seeder.ImportPreservedPushDevicesAsync(db, path, NullLogger.Instance);

        Assert.Equal(1, n);
        var d = Assert.Single(await db.PushDevices.ToListAsync());
        Assert.Equal((Ana, "ExponentPushToken[keep-me]", "ios"), (d.UserId, d.ExpoToken, d.Platform));
        Assert.Equal(new DateTime(2026, 11, 22, 10, 18, 22, DateTimeKind.Utc).Date, d.CreatedAt.Date);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(path + ".imported"));
        Assert.Equal(0, await Seeder.ImportPreservedPushDevicesAsync(db, path, NullLogger.Instance));   // nothing to do the second time
    }

    [Fact]
    public async Task A_broken_file_is_left_in_place_and_nothing_is_imported()
    {
        var path = Path.Combine(dir, "preserved-push-devices.json");
        File.WriteAllText(path, "{ not json");
        Assert.Equal(0, await Seeder.ImportPreservedPushDevicesAsync(db, path, NullLogger.Instance));
        Assert.True(File.Exists(path));
        Assert.Empty(await db.PushDevices.ToListAsync());
    }
}

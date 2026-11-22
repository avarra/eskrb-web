using Eskrb.Api.Data;
using Eskrb.Api.Domain;
using Eskrb.Api.Rules;
using Eskrb.Api.Services;
using Eskrb.Api.Services.Media;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eskrb.Api.Tests;

/// <summary>The "demo" seed profile for the judges: ten patients, history up to yesterday, nobody checked in today, all paired.</summary>
public class DemoSeedTests : IAsyncLifetime
{
    private sealed class MemoryMedia : IMediaStore
    {
        public Task<string> SaveAsync(Stream content, string extension, CancellationToken ct) => Task.FromResult(Guid.NewGuid().ToString("N") + extension);
        public Stream Open(string key) => throw new NotSupportedException();
    }

    private readonly SqliteConnection conn = new("Data Source=:memory:");
    private EskrbDb db = null!;

    public async Task InitializeAsync()
    {
        conn.Open();
        db = new EskrbDb(new DbContextOptionsBuilder<EskrbDb>().UseSqlite(conn).Options);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Seed:Profile"] = "demo" }).Build();
        await Seeder.SeedAsync(db, cfg, NullLogger.Instance, new MemoryMedia());
    }

    public Task DisposeAsync() { db.Dispose(); conn.Dispose(); return Task.CompletedTask; }

    [Fact]
    public async Task Ten_patients_with_history_but_no_check_in_today_in_different_stages()
    {
        var today = AppTime.Today;
        var patients = await db.Users.Where(u => u.Role == UserRole.Patient).ToListAsync();
        Assert.Equal(10, patients.Count);
        var episodes = await db.Episodes.ToListAsync();
        Assert.Equal(10, episodes.Count);
        Assert.All(episodes, e => Assert.True(e.IsActive(today)));
        Assert.Equal(10, episodes.Select(e => e.StartDate.DayNumber).Distinct().Count());   // all at different stages

        var checkIns = await db.CheckIns.ToListAsync();
        Assert.DoesNotContain(checkIns, c => c.Date >= today);                              // the judges do today's
        foreach (var e in episodes)
            Assert.Contains(checkIns, c => c.EpisodeId == e.Id);                            // everyone has history
        Assert.DoesNotContain(await db.Logs.ToListAsync(), l => AppTime.ToLocalDate(l.CreatedAt) >= today);
        Assert.DoesNotContain(await db.Flags.ToListAsync(), f => AppTime.ToLocalDate(f.CreatedAt) >= today);
    }

    [Fact]
    public async Task Everyone_is_in_exactly_one_running_buddy_pair_with_a_conversation()
    {
        var today = AppTime.Today;
        var pairs = await db.BuddyPairs.Include(p => p.EpisodeA).Include(p => p.EpisodeB).ToListAsync();
        Assert.Equal(5, pairs.Count);
        Assert.All(pairs, p => Assert.True(p.IsRunning(today)));
        var members = pairs.SelectMany(p => new[] { p.PatientAId, p.PatientBId }).ToList();
        Assert.Equal(10, members.Distinct().Count());
        Assert.All(pairs, p => Assert.Equal(p.EpisodeA!.ProtocolId, p.EpisodeB!.ProtocolId));

        foreach (var p in pairs)
        {
            var msgs = await db.BuddyMessages.Where(m => m.PairId == p.Id).ToListAsync();
            Assert.True(msgs.Count >= 3, "each pair has a conversation");
            Assert.Contains(msgs, m => m.FromUserId == p.PatientAId);
            Assert.Contains(msgs, m => m.FromUserId == p.PatientBId);
            Assert.All(msgs, m => Assert.True(m.CreatedAt <= DateTime.UtcNow));
        }
    }

    [Fact]
    public async Task The_triage_board_has_something_in_every_column_before_the_judges_start()
    {
        var today = AppTime.Today;
        var eps = await db.Episodes.ToListAsync();
        var openFlags = (await db.Flags.Where(f => f.ResolvedAt == null).ToListAsync()).Select(f => f.EpisodeId).ToHashSet();
        var dates = (await db.CheckIns.ToListAsync()).GroupBy(c => c.EpisodeId).ToDictionary(g => g.Key, g => g.Select(c => c.Date).ToHashSet());
        var lastMsg = (await db.Messages.Include(m => m.FromUser).ToListAsync()).GroupBy(m => m.EpisodeId).ToDictionary(g => g.Key, g => g.OrderBy(m => m.CreatedAt).Last());

        // judge the board at 10:00 (before the missed-check-in cutoff)
        var columns = eps.Select(e => Triage.Column(openFlags.Contains(e.Id),
            Triage.IsMissed(today, 10, 20, e.StartDate, dates[e.Id].Contains(today), dates[e.Id].Contains(today.AddDays(-1))),
            lastMsg.TryGetValue(e.Id, out var m) && m.FromUser!.Role != UserRole.Patient)).ToList();
        foreach (var c in new[] { Triage.NeedsAttention, Triage.MissedCheckIn, Triage.AwaitingPatientReply, Triage.Stable })
            Assert.Contains(c, columns);
    }

    [Fact]
    public async Task Staff_are_in_both_wings_and_the_wound_photo_story_exists_for_patients_past_day_two()
    {
        Assert.Equal(2, await db.DoctorGroups.CountAsync());
        Assert.Equal(4, await db.DoctorGroupMembers.CountAsync());   // doctor + senior in both wings
        var withPhotos = (await db.Logs.Where(l => l.Type == "photo").ToListAsync()).Select(l => l.EpisodeId).Distinct().Count();
        Assert.True(withPhotos >= 8);
    }
}

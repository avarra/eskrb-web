using System.Text.RegularExpressions;
using Eskrb.Api.Data;
using Eskrb.Api.Domain;
using Eskrb.Api.Integrations;
using Eskrb.Api.Rules;
using Eskrb.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Eskrb.Api.Controllers;

public record BuddyView(string FirstName, int DayNumber, bool CheckedInToday, int Streak, int MissedDays, bool Finished);
public record CheerDto(string Id, string Moment, string Label, string Text, string? Starter, bool Nudge);
public record BuddyDto(string Status, BuddyView? Buddy, int Unread, List<string> Moments, List<CheerDto> Cheers);
public record BuddyMessageDto(Guid Id, bool FromMe, string Kind, string Text, string? Source, DateTime CreatedAt);
public record SendBuddyText(string? Text, string? Source);
public record SendBuddyCheer(string? CheerId);

/// <summary>
/// The patient side of the recovery twin (docs/buddy.md). The buddy only ever learns first name, recovery day,
/// checked-in-today, streak, missed days and finished: never health data. Messages are peer chat: no LLM, no flags.
/// </summary>
[ApiController]
[Authorize(Roles = "patient")]
public class BuddyController(EskrbDb db, INotificationService notifications, ILogger<BuddyController> logger) : ControllerBase
{
    private const int MaxText = 300;
    private const int MaxTextsPerHour = 30;
    private const int MaxCheersPerDay = 3;
    private static readonly Regex Link = new(@"https?:|www\.", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private Guid Me => User.UserId();

    private static string First(string name) => name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? name;

    private async Task<BuddyFacts> FactsOfAsync(Episode e, Protocol? protocol, DateOnly today, CancellationToken ct)
    {
        var dates = (await db.CheckIns.Where(c => c.EpisodeId == e.Id).Select(c => c.Date).ToListAsync(ct)).ToHashSet();
        var duration = protocol?.DurationDays ?? (e.EndDate.DayNumber - e.StartDate.DayNumber);
        return Buddy.Facts(dates, today, e.StartDate, e.EndDate, duration);
    }

    [HttpGet("/me/buddy")]
    public async Task<ActionResult<BuddyDto>> Get(CancellationToken ct)
    {
        var pair = await db.LatestPairAsync(Me, ct);
        if (pair is null) return new BuddyDto("none", null, 0, [], []);

        var today = AppTime.Today;
        var running = pair.IsRunning(today);
        var other = pair.OtherEpisode(Me);
        var otherUser = await db.Users.FindAsync([pair.OtherPatient(Me)], ct);
        var protocol = await db.Protocols.FindAsync([other.ProtocolId], ct);
        var facts = await FactsOfAsync(other, protocol, today, ct);
        var myDone = await db.CheckIns.AnyAsync(c => c.EpisodeId == pair.MyEpisode(Me).Id && c.Date == today, ct);

        var unread = await db.BuddyMessages.CountAsync(m => m.PairId == pair.Id && m.FromUserId != Me && m.ReadAt == null && m.HiddenAt == null, ct);
        // Ended by a person (left / staff): the other side only learns "ended", never who or why.
        if (pair.EndedAt != null) return new BuddyDto("ended", null, unread, [], []);

        var moments = Buddy.Moments(facts, myDone);
        var cheers = Buddy.Catalogue
            .Where(c => running || (facts.Finished && c.Id == "congrats-finish"))
            .Select(c => new CheerDto(c.Id, c.Moment, c.Label, c.Text, c.Starter, c.Nudge)).ToList();
        return new BuddyDto(running ? "active" : "ended",
            new BuddyView(First(otherUser!.Name), facts.DayNumber, facts.CheckedInToday, facts.Streak, facts.MissedDays, facts.Finished),
            unread, running || facts.Finished ? moments : [], cheers);
    }

    /// <summary>The thread, oldest first (hidden messages excluded). <c>after</c> = only newer than that instant.</summary>
    [HttpGet("/me/buddy/messages")]
    public async Task<ActionResult<List<BuddyMessageDto>>> Messages(DateTime? after, CancellationToken ct)
    {
        var pair = await db.LatestPairAsync(Me, ct);
        if (pair is null || pair.EndedAt != null) return new List<BuddyMessageDto>();
        var q = db.BuddyMessages.Where(m => m.PairId == pair.Id && m.HiddenAt == null);
        if (after is { } a) q = q.Where(m => m.CreatedAt > a.ToUniversalTime());
        return (await q.ToListAsync(ct)).OrderBy(m => m.CreatedAt)
            .Select(m => new BuddyMessageDto(m.Id, m.FromUserId == Me, m.Kind, m.Text, m.Source, m.CreatedAt)).ToList();
    }

    [HttpPost("/me/buddy/messages")]
    public async Task<ActionResult<BuddyMessageDto>> SendText(SendBuddyText req, CancellationToken ct)
    {
        var pair = await RunningPairAsync(ct);
        if (pair is null) return Problem(detail: "You have no active buddy.", statusCode: 409);
        var text = req.Text?.Trim() ?? "";
        if (text.Length == 0 || text.Length > MaxText) return Problem(detail: $"text must be 1-{MaxText} characters.", statusCode: 400);
        if (Link.IsMatch(text)) return Problem(detail: "Links are not allowed in buddy messages.", statusCode: 400);
        var source = req.Source ?? "typed";
        if (source is not ("typed" or "dictated")) return Problem(detail: "source must be typed or dictated.", statusCode: 400);

        var hourAgo = DateTime.UtcNow.AddHours(-1);
        if (await db.BuddyMessages.CountAsync(m => m.PairId == pair.Id && m.FromUserId == Me && m.Kind == "text" && m.CreatedAt > hourAgo, ct) >= MaxTextsPerHour)
            return Problem(detail: "You are sending messages too quickly. Try again in a while.", statusCode: 429);

        var msg = await SaveAsync(pair, "text", null, text, source, ct);
        return StatusCode(201, ToDto(msg));
    }

    [HttpPost("/me/buddy/cheers")]
    public async Task<ActionResult<BuddyMessageDto>> SendCheer(SendBuddyCheer req, CancellationToken ct)
    {
        var cheer = Buddy.Find(req.CheerId);
        if (cheer is null) return Problem(detail: "Unknown cheerId.", statusCode: 400);

        var today = AppTime.Today;
        var pair = await db.LatestPairAsync(Me, ct);
        if (pair is null || pair.EndedAt != null) return Problem(detail: "You have no active buddy.", statusCode: 409);

        var other = pair.OtherEpisode(Me);
        var facts = await FactsOfAsync(other, await db.Protocols.FindAsync([other.ProtocolId], ct), today, ct);
        var running = pair.IsRunning(today);

        if (!running)
        {
            // The only thing still allowed after an episode is over: congratulate a buddy who finished, once.
            if (!(facts.Finished && cheer.Id == "congrats-finish"))
                return Problem(detail: "This buddy connection has ended.", statusCode: 409);
            if (await db.BuddyMessages.AnyAsync(m => m.PairId == pair.Id && m.FromUserId == Me && m.CheerId == cheer.Id, ct))
                return Problem(detail: "You already sent this.", statusCode: 409);
        }
        else
        {
            var myDone = await db.CheckIns.AnyAsync(c => c.EpisodeId == pair.MyEpisode(Me).Id && c.Date == today, ct);
            if (cheer.Moment != Buddy.Always && !Buddy.Moments(facts, myDone).Contains(cheer.Moment))
                return Problem(detail: "That cheer does not fit right now.", statusCode: 409);

            var dayStart = AppTime.AtLocal(today, 0);
            var todays = await db.BuddyMessages.Where(m => m.PairId == pair.Id && m.FromUserId == Me && m.Kind == "cheer" && m.CreatedAt >= dayStart).ToListAsync(ct);
            if (todays.Count >= MaxCheersPerDay) return Problem(detail: "You have sent enough cheers for today.", statusCode: 429);
            var nudges = Buddy.Catalogue.Where(c => c.Nudge).Select(c => c.Id).ToHashSet();
            if (cheer.Nudge && todays.Any(m => nudges.Contains(m.CheerId!)))
                return Problem(detail: "You already sent a reminder today.", statusCode: 429);
            if (cheer.Id == "ask" && await db.BuddyMessages.AnyAsync(m => m.PairId == pair.Id && m.FromUserId == Me && m.CheerId == "ask" && m.CreatedAt > DateTime.UtcNow.AddDays(-3), ct))
                return Problem(detail: "You already asked recently.", statusCode: 429);
        }

        var msg = await SaveAsync(pair, "cheer", cheer.Id, cheer.Text, null, ct);
        return StatusCode(201, ToDto(msg));
    }

    [HttpPost("/me/buddy/read")]
    public async Task<IActionResult> Read(CancellationToken ct)
    {
        var pair = await db.LatestPairAsync(Me, ct);
        if (pair is null) return NoContent();
        var now = DateTime.UtcNow;
        foreach (var m in await db.BuddyMessages.Where(m => m.PairId == pair.Id && m.FromUserId != Me && m.ReadAt == null).ToListAsync(ct)) m.ReadAt = now;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("/me/buddy/messages/{id:guid}/report")]
    public async Task<IActionResult> Report(Guid id, CancellationToken ct)
    {
        var pair = await db.LatestPairAsync(Me, ct);
        var msg = pair is null ? null : await db.BuddyMessages.FirstOrDefaultAsync(m => m.Id == id && m.PairId == pair.Id && m.FromUserId != Me, ct);
        if (msg is null) return NotFound();
        msg.ReportedAt ??= DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("/me/buddy")]
    public async Task<IActionResult> Leave(CancellationToken ct)
    {
        var pair = await RunningPairAsync(ct);
        if (pair is null) return NotFound();
        pair.EndedAt = DateTime.UtcNow;
        pair.EndedBy = Me;
        pair.EndReason = "left";
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task<BuddyPair?> RunningPairAsync(CancellationToken ct)
    {
        var pair = await db.LatestPairAsync(Me, ct);
        return pair is not null && pair.IsRunning(AppTime.Today) ? pair : null;
    }

    private BuddyMessageDto ToDto(BuddyMessage m) => new(m.Id, m.FromUserId == Me, m.Kind, m.Text, m.Source, m.CreatedAt);

    private async Task<BuddyMessage> SaveAsync(BuddyPair pair, string kind, string? cheerId, string text, string? source, CancellationToken ct)
    {
        var previous = await db.BuddyMessages.Where(m => m.PairId == pair.Id && m.FromUserId == Me)
            .OrderByDescending(m => m.CreatedAt).Select(m => (DateTime?)m.CreatedAt).FirstOrDefaultAsync(ct);
        var msg = new BuddyMessage
        {
            Id = Guid.NewGuid(), PairId = pair.Id, FromUserId = Me, Kind = kind, CheerId = cheerId, Text = text, Source = source, CreatedAt = DateTime.UtcNow,
        };
        db.BuddyMessages.Add(msg);
        await db.SaveChangesAsync(ct);

        // Generic push, at most one per pair per 10 minutes, none at night. A push failure never fails the request.
        if (!Buddy.IsQuietHour(AppTime.Now.Hour) && (previous is null || msg.CreatedAt - previous.Value > TimeSpan.FromMinutes(10)))
        {
            try
            {
                await notifications.SendToUserAsync(pair.OtherPatient(Me), new PushMessage(
                    "eSkrb", kind == "cheer" ? "Kolega vam pošilja pozdrav." : "Imate novo sporočilo od kolega.",
                    new() { ["kind"] = "buddy" }), ct);
            }
            catch (Exception ex) { logger.LogWarning(ex, "Buddy push failed"); }
        }
        return msg;
    }
}

using Eskrb.Api.Data;
using Eskrb.Api.Domain;
using Eskrb.Api.Rules;
using Eskrb.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Eskrb.Api.Controllers;

public record TriageFlag(Guid Id, string RuleId, string Severity, string Quote, DateTime CreatedAt);
public record TriageEscalation(Guid Id, string SourceType, Guid SourceId, string FromName, int ToLevel, string? Comment, DateTime CreatedAt);
public record TriageCard(
    Guid EpisodeId, Guid PatientId, string PatientName, string GroupName, string ProtocolName, int DayNumber,
    TriageFlag? TopFlag, int OpenFlags, bool MissedCheckIn, DateOnly? LastCheckInDate,
    DateTime? LastMessageAt, bool LastMessageFromStaff, TriageEscalation? Escalation, TriageReview? LastReview);
public record TriageDto(
    List<TriageCard> NeedsAttention, List<TriageCard> MissedCheckIn, List<TriageCard> AwaitingPatientReply,
    List<TriageCard> Stable, DateTime GeneratedAt);
public record ReviewRequest(string? Status, string? Comment);
public record ReviewResultDto(int CheckIns, int Logs, int FlagsResolved, int EscalationsResolved);
public record ReviewDto(Guid Id, string Status, string? Comment, string ReviewerName, DateTime CreatedAt, int CheckIns, int Logs, int FlagsResolved);
public record TriageReview(string Status, string? Comment, string ReviewerName, DateTime CreatedAt);

[ApiController]
[Authorize(Roles = "staff,admin")]
public class StaffController(EskrbDb db, IConfiguration config) : ControllerBase
{
    /// <summary>
    /// The triage board: every ACTIVE episode the caller can see, in exactly one column (first match wins):
    /// needsAttention (unresolved flag; urgent first), missedCheckIn, awaitingPatientReply (last message is from staff), stable.
    /// Each card also carries the missedCheckIn marker. Cutoff hour: config Triage:MissedCutoffHour (default 20, Europe/Ljubljana).
    /// </summary>
    [HttpGet("/staff/triage")]
    public async Task<ActionResult<TriageDto>> Get(CancellationToken ct)
    {
        var today = AppTime.Today;
        var hour = AppTime.Now.Hour;
        var cutoff = config.GetValue("Triage:MissedCutoffHour", 20);

        var episodes = await db.VisibleEpisodes(User)
            .Where(e => e.StartDate <= today && e.EndDate >= today)
            .Include(e => e.Patient).Include(e => e.DoctorGroup).Include(e => e.Protocol)
            .ToListAsync(ct);
        var ids = episodes.Select(e => e.Id).ToList();

        var openFlags = (await db.Flags.Where(f => ids.Contains(f.EpisodeId) && f.ResolvedAt == null).ToListAsync(ct))
            .GroupBy(f => f.EpisodeId).ToDictionary(g => g.Key, g => g.ToList());
        var checkInDates = (await db.CheckIns.Where(c => ids.Contains(c.EpisodeId)).Select(c => new { c.EpisodeId, c.Date }).ToListAsync(ct))
            .GroupBy(c => c.EpisodeId).ToDictionary(g => g.Key, g => g.Select(c => c.Date).ToHashSet());
        var lastMessages = (await db.Messages.Where(m => ids.Contains(m.EpisodeId)).Include(m => m.FromUser).ToListAsync(ct))
            .GroupBy(m => m.EpisodeId).ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.CreatedAt).First());

        // Open escalations the caller must act on: addressed to their level (or below it), or anything for an admin.
        var myLevels = User.IsAdmin() ? [] : await db.ActiveMemberships(User.UserId()).ToDictionaryAsync(m => m.GroupId, m => m.Level, ct);
        var escalations = (await db.Escalations.Include(x => x.FromUser).Where(x => ids.Contains(x.EpisodeId) && x.ResolvedAt == null).ToListAsync(ct))
            .Where(x => User.IsAdmin() || (myLevels.TryGetValue(episodes.First(e => e.Id == x.EpisodeId).DoctorGroupId, out var lv) && lv >= x.ToLevel))
            .GroupBy(x => x.EpisodeId).ToDictionary(g => g.Key, g => g.OrderBy(x => x.CreatedAt).First());

        var lastReviews = (await db.EpisodeReviews.Include(r => r.Reviewer).Where(r => ids.Contains(r.EpisodeId)).ToListAsync(ct))
            .GroupBy(r => r.EpisodeId).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.CreatedAt).First());

        var cards = new List<(string Column, TriageCard Card)>();
        foreach (var e in episodes)
        {
            var dates = checkInDates.GetValueOrDefault(e.Id) ?? [];
            var flags = openFlags.GetValueOrDefault(e.Id) ?? [];
            var top = flags.OrderBy(f => f.Severity == "urgent" ? 0 : 1).ThenBy(f => f.CreatedAt).FirstOrDefault();
            var last = lastMessages.GetValueOrDefault(e.Id);
            var fromStaff = last is not null && last.FromUser!.Role != UserRole.Patient;
            var missed = Triage.IsMissed(today, hour, cutoff, e.StartDate, dates.Contains(today), dates.Contains(today.AddDays(-1)));
            var esc = escalations.GetValueOrDefault(e.Id);
            var column = Triage.Column(top is not null || esc is not null, missed, fromStaff);
            cards.Add((column, new TriageCard(
                e.Id, e.PatientId, e.Patient!.Name, e.DoctorGroup!.Name, e.Protocol!.Name,
                today.DayNumber - e.StartDate.DayNumber + 1,
                top is null ? null : new TriageFlag(top.Id, top.RuleId, top.Severity, top.Quote, top.CreatedAt),
                flags.Count, missed, dates.Count == 0 ? null : dates.Max(), last?.CreatedAt, fromStaff,
                esc is null ? null : new TriageEscalation(esc.Id, esc.SourceType, esc.SourceId, esc.FromUser!.Name, esc.ToLevel, esc.Comment, esc.CreatedAt),
                lastReviews.TryGetValue(e.Id, out var rv) ? new TriageReview(rv.Status, rv.Comment, rv.Reviewer!.Name, rv.CreatedAt) : null)));
        }

        List<TriageCard> Col(string name, Func<IEnumerable<TriageCard>, IEnumerable<TriageCard>> order) =>
            order(cards.Where(c => c.Column == name).Select(c => c.Card)).ToList();
        return new TriageDto(
            Col(Triage.NeedsAttention, c => c
                .OrderBy(x => x.TopFlag?.Severity == "urgent" ? 0 : x.Escalation is not null ? 1 : 2)
                .ThenBy(x => x.TopFlag?.CreatedAt ?? x.Escalation!.CreatedAt)),
            Col(Triage.MissedCheckIn, c => c.OrderBy(x => x.LastCheckInDate ?? DateOnly.MinValue)),
            Col(Triage.AwaitingPatientReply, c => c.OrderBy(x => x.LastMessageAt)),
            Col(Triage.Stable, c => c.OrderBy(x => x.PatientName)),
            DateTime.UtcNow);
    }

    /// <summary>
    /// Marks every unreviewed check-in and note of the episode as reviewed by the caller and resolves its open flags,
    /// recording the outcome: body <c>{ status: no_action|monitor|contacted|referred, comment? }</c> (optional; default no_action).
    /// </summary>
    [HttpPost("/staff/episodes/{id:guid}/review")]
    public async Task<ActionResult<ReviewResultDto>> Review(Guid id, ReviewRequest? req, CancellationToken ct)
    {
        var status = req?.Status ?? ReviewStatuses.NoAction;
        if (!ReviewStatuses.All.Contains(status)) return Problem(detail: $"status must be one of: {string.Join(", ", ReviewStatuses.All)}.", statusCode: 400);
        if (req?.Comment is { Length: > 500 }) return Problem(detail: "comment is longer than 500 characters.", statusCode: 400);
        var episode = await db.VisibleEpisodes(User).FirstOrDefaultAsync(e => e.Id == id, ct);
        if (episode is null) return NotFound();
        var now = DateTime.UtcNow;
        var me = User.UserId();

        var checkIns = await db.CheckIns.Where(c => c.EpisodeId == id && c.ReviewedAt == null).ToListAsync(ct);
        var logs = await db.Logs.Where(l => l.EpisodeId == id && l.ReviewedAt == null).ToListAsync(ct);
        var flags = await db.Flags.Where(f => f.EpisodeId == id && f.ResolvedAt == null).ToListAsync(ct);
        foreach (var c in checkIns) { c.ReviewedAt = now; c.ReviewedBy = me; }
        foreach (var l in logs) { l.ReviewedAt = now; l.ReviewedBy = me; }
        foreach (var f in flags) f.ResolvedAt = now;
        // Escalations addressed to the reviewer's level (or below) are handled by this review too.
        var level = User.IsAdmin() ? int.MaxValue : await EscalationsController.LevelInAsync(db, me, episode.DoctorGroupId, ct) ?? 0;
        var escs = (await db.Escalations.Where(x => x.EpisodeId == id && x.ResolvedAt == null).ToListAsync(ct)).Where(x => level >= x.ToLevel).ToList();
        foreach (var x in escs) { x.ResolvedAt = now; x.ResolvedBy = me; }
        db.EpisodeReviews.Add(new EpisodeReview
        {
            Id = Guid.NewGuid(), EpisodeId = id, ReviewedBy = me, Status = status, CreatedAt = now,
            Comment = string.IsNullOrWhiteSpace(req?.Comment) ? null : req!.Comment!.Trim(),
            CheckIns = checkIns.Count, Logs = logs.Count, FlagsResolved = flags.Count,
        });
        await db.SaveChangesAsync(ct);
        return new ReviewResultDto(checkIns.Count, logs.Count, flags.Count, escs.Count);
    }

    /// <summary>Review history of an episode (newest first).</summary>
    [HttpGet("/staff/episodes/{id:guid}/reviews")]
    public async Task<ActionResult<List<ReviewDto>>> Reviews(Guid id, CancellationToken ct)
    {
        if (!await db.VisibleEpisodes(User).AnyAsync(e => e.Id == id, ct)) return NotFound();
        return (await db.EpisodeReviews.Include(r => r.Reviewer).Where(r => r.EpisodeId == id).ToListAsync(ct))
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new ReviewDto(r.Id, r.Status, r.Comment, r.Reviewer!.Name, r.CreatedAt, r.CheckIns, r.Logs, r.FlagsResolved)).ToList();
    }
}

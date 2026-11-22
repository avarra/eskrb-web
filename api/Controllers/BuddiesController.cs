using Eskrb.Api.Data;
using Eskrb.Api.Domain;
using Eskrb.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Eskrb.Api.Controllers;

public record BuddySide(Guid EpisodeId, Guid PatientId, string PatientName, int DayNumber, string GroupName);
public record BuddyPairDto(
    Guid Id, BuddySide A, BuddySide B, string ProtocolName, string CreatedByName, DateTime CreatedAt,
    string Status, string? EndReason, int Messages, int Reported);
public record CreatePairRequest(Guid EpisodeAId, Guid EpisodeBId, bool ConsentConfirmed);
public record CandidateDto(Guid EpisodeId, Guid PatientId, string PatientName, int DayNumber, string GroupName, int StartDiffDays);
public record StaffBuddyMessageDto(Guid Id, string FromName, string Kind, string Text, string? Source, DateTime CreatedAt, bool Reported, bool Hidden);

/// <summary>Staff side of the recovery twin: pair patients by hand, read threads, hide messages, unpair. Scoped by doctor group.</summary>
[ApiController]
[Authorize(Roles = "staff,admin")]
public class BuddiesController(EskrbDb db) : ControllerBase
{
    private static int Day(Episode e, DateOnly today) => Math.Max(1, today.DayNumber - e.StartDate.DayNumber + 1);

    [HttpGet("/staff/buddies")]
    public async Task<List<BuddyPairDto>> List(CancellationToken ct)
    {
        var visible = db.VisibleEpisodes(User).Select(e => e.Id);
        var pairs = await db.BuddyPairs
            .Include(p => p.EpisodeA).ThenInclude(e => e!.Patient).Include(p => p.EpisodeA).ThenInclude(e => e!.DoctorGroup).Include(p => p.EpisodeA).ThenInclude(e => e!.Protocol)
            .Include(p => p.EpisodeB).ThenInclude(e => e!.Patient).Include(p => p.EpisodeB).ThenInclude(e => e!.DoctorGroup)
            .Include(p => p.Creator)
            .Where(p => visible.Contains(p.EpisodeAId) || visible.Contains(p.EpisodeBId)).ToListAsync(ct);
        var ids = pairs.Select(p => p.Id).ToList();
        var counts = (await db.BuddyMessages.Where(m => ids.Contains(m.PairId)).Select(m => new { m.PairId, Reported = m.ReportedAt != null }).ToListAsync(ct))
            .GroupBy(m => m.PairId).ToDictionary(g => g.Key, g => (Total: g.Count(), Reported: g.Count(m => m.Reported)));

        var today = AppTime.Today;
        return pairs.OrderByDescending(p => p.CreatedAt).Select(p =>
        {
            var c = counts.GetValueOrDefault(p.Id);
            var status = p.IsRunning(today) ? "active" : "ended";
            var reason = p.EndedAt != null ? p.EndReason : status == "ended" ? "episode_ended" : null;
            return new BuddyPairDto(p.Id, Side(p.EpisodeA!, today), Side(p.EpisodeB!, today), p.EpisodeA!.Protocol!.Name,
                p.Creator!.Name, p.CreatedAt, status, reason, c.Total, c.Reported);
        }).ToList();
    }

    private static BuddySide Side(Episode e, DateOnly today) =>
        new(e.Id, e.PatientId, e.Patient!.Name, Day(e, today), e.DoctorGroup!.Name);

    /// <summary>Suggested partners for an episode: active, same protocol, no running buddy, closest start date first.</summary>
    [HttpGet("/staff/buddies/candidates")]
    public async Task<ActionResult<List<CandidateDto>>> Candidates(Guid episodeId, CancellationToken ct)
    {
        var ep = await db.VisibleEpisodes(User).FirstOrDefaultAsync(e => e.Id == episodeId, ct);
        if (ep is null) return NotFound();
        var today = AppTime.Today;
        var paired = (await db.Running(today).Select(p => new { p.PatientAId, p.PatientBId }).ToListAsync(ct))
            .SelectMany(p => new[] { p.PatientAId, p.PatientBId }).ToHashSet();

        var others = await db.VisibleEpisodes(User).Include(e => e.Patient).Include(e => e.DoctorGroup)
            .Where(e => e.ProtocolId == ep.ProtocolId && e.PatientId != ep.PatientId && e.StartDate <= today && e.EndDate >= today).ToListAsync(ct);
        return others.Where(e => !paired.Contains(e.PatientId))
            .Select(e => new CandidateDto(e.Id, e.PatientId, e.Patient!.Name, Day(e, today), e.DoctorGroup!.Name, Math.Abs(e.StartDate.DayNumber - ep.StartDate.DayNumber)))
            .OrderBy(c => c.StartDiffDays).ThenBy(c => c.PatientName).ToList();
    }

    [HttpPost("/staff/buddies")]
    public async Task<ActionResult<BuddyPairDto>> Create(CreatePairRequest req, CancellationToken ct)
    {
        if (!req.ConsentConfirmed)
            return Problem(detail: "Confirm that both patients agreed to be paired and know that staff can read their messages.", statusCode: 400);
        if (req.EpisodeAId == req.EpisodeBId) return Problem(detail: "Pick two different episodes.", statusCode: 400);

        var today = AppTime.Today;
        var eps = await db.VisibleEpisodes(User).Include(e => e.Patient).Include(e => e.DoctorGroup).Include(e => e.Protocol)
            .Where(e => e.Id == req.EpisodeAId || e.Id == req.EpisodeBId).ToListAsync(ct);
        var a = eps.FirstOrDefault(e => e.Id == req.EpisodeAId);
        var b = eps.FirstOrDefault(e => e.Id == req.EpisodeBId);
        if (a is null || b is null) return NotFound();
        if (a.PatientId == b.PatientId) return Problem(detail: "Pick two different patients.", statusCode: 400);
        if (a.ProtocolId != b.ProtocolId) return Problem(detail: "Buddies must be on the same protocol.", statusCode: 400);
        if (!a.IsActive(today) || !b.IsActive(today)) return Problem(detail: "Both episodes must be active.", statusCode: 409);
        if (await db.Running(today).AnyAsync(p => p.PatientAId == a.PatientId || p.PatientBId == a.PatientId || p.PatientAId == b.PatientId || p.PatientBId == b.PatientId, ct))
            return Problem(detail: "One of these patients already has a buddy.", statusCode: 409);

        var pair = new BuddyPair
        {
            Id = Guid.NewGuid(), EpisodeAId = a.Id, EpisodeBId = b.Id, PatientAId = a.PatientId, PatientBId = b.PatientId,
            CreatedBy = User.UserId(), CreatedAt = DateTime.UtcNow,
        };
        db.BuddyPairs.Add(pair);
        await db.SaveChangesAsync(ct);
        var creator = await db.Users.FindAsync([pair.CreatedBy], ct);
        return StatusCode(201, new BuddyPairDto(pair.Id, Side(a, today), Side(b, today), a.Protocol!.Name, creator!.Name, pair.CreatedAt, "active", null, 0, 0));
    }

    [HttpDelete("/staff/buddies/{id:guid}")]
    public async Task<IActionResult> Unpair(Guid id, CancellationToken ct)
    {
        var pair = await Visible(id, ct);
        if (pair is null) return NotFound();
        if (pair.EndedAt == null)
        {
            pair.EndedAt = DateTime.UtcNow;
            pair.EndedBy = User.UserId();
            pair.EndReason = "staff";
            await db.SaveChangesAsync(ct);
        }
        return NoContent();
    }

    [HttpGet("/staff/buddies/{id:guid}/messages")]
    public async Task<ActionResult<List<StaffBuddyMessageDto>>> Messages(Guid id, CancellationToken ct)
    {
        var pair = await Visible(id, ct);
        if (pair is null) return NotFound();
        var names = await db.Users.Where(u => u.Id == pair.PatientAId || u.Id == pair.PatientBId).ToDictionaryAsync(u => u.Id, u => u.Name, ct);
        return (await db.BuddyMessages.Where(m => m.PairId == id).ToListAsync(ct)).OrderBy(m => m.CreatedAt)
            .Select(m => new StaffBuddyMessageDto(m.Id, names.GetValueOrDefault(m.FromUserId, "?"), m.Kind, m.Text, m.Source, m.CreatedAt, m.ReportedAt != null, m.HiddenAt != null)).ToList();
    }

    [HttpDelete("/staff/buddies/{id:guid}/messages/{messageId:guid}")]
    public async Task<IActionResult> Hide(Guid id, Guid messageId, CancellationToken ct)
    {
        if (await Visible(id, ct) is null) return NotFound();
        var msg = await db.BuddyMessages.FirstOrDefaultAsync(m => m.Id == messageId && m.PairId == id, ct);
        if (msg is null) return NotFound();
        msg.HiddenAt ??= DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>A pair the caller can see (staff see a pair if they can see either patient's episode).</summary>
    private async Task<BuddyPair?> Visible(Guid id, CancellationToken ct)
    {
        var visible = db.VisibleEpisodes(User).Select(e => e.Id);
        return await db.BuddyPairs.FirstOrDefaultAsync(p => p.Id == id && (visible.Contains(p.EpisodeAId) || visible.Contains(p.EpisodeBId)), ct);
    }
}

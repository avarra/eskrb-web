using Eskrb.Api.Data;
using Eskrb.Api.Domain;
using Eskrb.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Eskrb.Api.Controllers;

public record EscalateRequest(string? SourceType, Guid SourceId, string? Comment);
public record EscalationDto(
    Guid Id, string SourceType, Guid SourceId, string FromName, int ToLevel, string? Comment,
    DateTime CreatedAt, bool Resolved, DateTime? ResolvedAt);

/// <summary>
/// Escalation: a lower-level group member hands an item up to the next level above them (lowest active level higher
/// than theirs in the episode's group). It shows in that level's triage "needs attention" until resolved.
/// </summary>
[ApiController]
[Authorize(Roles = "staff,admin")]
public class EscalationsController(EskrbDb db) : ControllerBase
{
    private static readonly string[] SourceTypes = ["checkin", "log", "message"];

    public static EscalationDto ToDto(Escalation e) => new(
        e.Id, e.SourceType, e.SourceId, e.FromUser?.Name ?? "", e.ToLevel, e.Comment, e.CreatedAt, e.ResolvedAt != null, e.ResolvedAt);

    /// <summary>The caller's active level in the group (null = no active membership, e.g. an admin).</summary>
    public static async Task<int?> LevelInAsync(EskrbDb db, Guid userId, Guid groupId, CancellationToken ct)
    {
        var levels = await db.ActiveMemberships(userId).Where(m => m.GroupId == groupId).Select(m => (int?)m.Level).ToListAsync(ct);
        return levels.Count == 0 ? null : levels.Max();
    }

    [HttpGet("/episodes/{id:guid}/escalations")]
    public async Task<ActionResult<List<EscalationDto>>> List(Guid id, CancellationToken ct)
    {
        var (ep, _) = await db.ResolveAsync(User, id);
        if (ep is null) return NotFound();
        return (await db.Escalations.Include(e => e.FromUser).Where(e => e.EpisodeId == id).ToListAsync(ct))
            .OrderByDescending(e => e.CreatedAt).Select(ToDto).ToList();
    }

    [HttpPost("/episodes/{id:guid}/escalations")]
    public async Task<ActionResult<EscalationDto>> Create(Guid id, EscalateRequest req, CancellationToken ct)
    {
        var (ep, _) = await db.ResolveAsync(User, id);
        if (ep is null) return NotFound();
        if (req.SourceType is null || !SourceTypes.Contains(req.SourceType))
            return Problem(detail: $"sourceType must be one of: {string.Join(", ", SourceTypes)}.", statusCode: 400);
        if (req.Comment is { Length: > 500 }) return Problem(detail: "comment is longer than 500 characters.", statusCode: 400);

        var exists = req.SourceType switch
        {
            "checkin" => await db.CheckIns.AnyAsync(c => c.Id == req.SourceId && c.EpisodeId == id, ct),
            "log" => await db.Logs.AnyAsync(l => l.Id == req.SourceId && l.EpisodeId == id, ct),
            _ => await db.Messages.AnyAsync(m => m.Id == req.SourceId && m.EpisodeId == id, ct),
        };
        if (!exists) return Problem(detail: "That item does not belong to this episode.", statusCode: 404);

        var me = User.UserId();
        if (await LevelInAsync(db, me, ep.DoctorGroupId, ct) is not { } myLevel)
            return Problem(detail: "Only members of the episode's doctor group can escalate.", statusCode: 409);
        var above = await db.DoctorGroupMembers
            .Where(m => m.GroupId == ep.DoctorGroupId && m.Level > myLevel).ToListAsync(ct);
        var now = DateTime.UtcNow;
        var targets = above.Where(m => (m.ActiveFrom == null || m.ActiveFrom <= now) && (m.ActiveUntil == null || m.ActiveUntil > now)).ToList();
        if (targets.Count == 0) return Problem(detail: "There is no one above you in this group to escalate to.", statusCode: 409);

        var e = new Escalation
        {
            Id = Guid.NewGuid(), EpisodeId = id, SourceType = req.SourceType, SourceId = req.SourceId, FromUserId = me,
            ToLevel = targets.Min(m => m.Level), Comment = string.IsNullOrWhiteSpace(req.Comment) ? null : req.Comment.Trim(),
            CreatedAt = now,
        };
        db.Escalations.Add(e);
        await db.SaveChangesAsync(ct);
        e.FromUser = await db.Users.FindAsync([me], ct);
        return StatusCode(201, ToDto(e));
    }

    /// <summary>Resolve an escalation: staff at or above the target level (or an admin).</summary>
    [HttpPost("/episodes/{id:guid}/escalations/{escalationId:guid}/resolve")]
    public async Task<ActionResult<EscalationDto>> Resolve(Guid id, Guid escalationId, CancellationToken ct)
    {
        var (ep, _) = await db.ResolveAsync(User, id);
        if (ep is null) return NotFound();
        var e = await db.Escalations.Include(x => x.FromUser).FirstOrDefaultAsync(x => x.Id == escalationId && x.EpisodeId == id, ct);
        if (e is null) return NotFound();
        if (!User.IsAdmin() && !(await LevelInAsync(db, User.UserId(), ep.DoctorGroupId, ct) >= e.ToLevel))
            return Problem(detail: "Only staff at or above the escalated level can resolve this.", statusCode: 403);
        e.ResolvedAt ??= DateTime.UtcNow;
        e.ResolvedBy ??= User.UserId();
        await db.SaveChangesAsync(ct);
        return ToDto(e);
    }
}

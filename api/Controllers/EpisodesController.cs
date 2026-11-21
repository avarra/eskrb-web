using Eskrb.Api.Data;
using Eskrb.Api.Domain;
using Eskrb.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Eskrb.Api.Controllers;

public record CreateEpisodeRequest(Guid PatientId, string ProtocolId, Guid DoctorGroupId, DateOnly? StartDate);
public record UpdateEpisodeRequest(DateOnly? StartDate, DateOnly? EndDate);
public record SetEpisodeGroupRequest(Guid DoctorGroupId);

/// <summary>
/// Episode writes. Create is admin-only; edit/end/switch-group are staff+admin, and staff are scoped to
/// episodes (and target groups) they are an active member of.
/// </summary>
[ApiController]
public class EpisodesController(EskrbDb db) : ControllerBase
{
    private ActionResult Fail(int status, string detail) => Problem(detail: detail, statusCode: status);

    private Task<Episode?> LoadVisible(Guid id) =>
        db.VisibleEpisodes(User)
            .Include(e => e.Patient).Include(e => e.Protocol).Include(e => e.DoctorGroup)
            .FirstOrDefaultAsync(e => e.Id == id);

    /// <summary>True if the patient has another episode whose dates overlap [start, end].</summary>
    private Task<bool> Overlaps(Guid patientId, DateOnly start, DateOnly end, Guid? exceptId = null) =>
        db.Episodes.AnyAsync(e => e.PatientId == patientId && e.Id != exceptId
            && e.StartDate <= end && e.EndDate >= start);

    /// <summary>Assign a protocol to a patient. End date = start + the protocol's duration.</summary>
    [HttpPost("/episodes")]
    [Authorize(Roles = "admin")]
    public async Task<ActionResult<EpisodeListDto>> Create(CreateEpisodeRequest req)
    {
        var patient = await db.Users.FindAsync(req.PatientId);
        if (patient is null || patient.Role != UserRole.Patient) return Fail(400, "patientId must be an existing patient.");
        var protocol = await db.Protocols.FindAsync(req.ProtocolId);
        if (protocol is null) return Fail(400, $"Unknown protocol '{req.ProtocolId}'.");
        if (!await db.DoctorGroups.AnyAsync(g => g.Id == req.DoctorGroupId)) return Fail(400, "Unknown doctor group.");

        var start = req.StartDate ?? AppTime.Today;
        var end = start.AddDays(protocol.DurationDays);
        if (await Overlaps(patient.Id, start, end)) return Fail(409, "Patient already has an episode overlapping these dates.");

        var ep = new Episode
        {
            Id = Guid.NewGuid(), PatientId = patient.Id, ProtocolId = protocol.Id,
            DoctorGroupId = req.DoctorGroupId, StartDate = start, EndDate = end,
        };
        db.Episodes.Add(ep);
        await db.SaveChangesAsync();

        var created = await LoadVisible(ep.Id);
        return StatusCode(201, created!.ToListDto(AppTime.Today));
    }

    /// <summary>Edit start and/or end date.</summary>
    [HttpPatch("/episodes/{id:guid}")]
    [Authorize(Roles = "staff,admin")]
    public async Task<ActionResult<EpisodeListDto>> Update(Guid id, UpdateEpisodeRequest req)
    {
        var ep = await LoadVisible(id);
        if (ep is null) return NotFound();

        var start = req.StartDate ?? ep.StartDate;
        var end = req.EndDate ?? ep.EndDate;
        if (end < start) return Fail(400, "endDate must not be before startDate.");
        if (await Overlaps(ep.PatientId, start, end, ep.Id)) return Fail(409, "Dates overlap another episode of this patient.");

        ep.StartDate = start;
        ep.EndDate = end;
        await db.SaveChangesAsync();
        return ep.ToListDto(AppTime.Today);
    }

    /// <summary>
    /// End the episode now: its end date becomes yesterday (never before its start), so it is inactive
    /// immediately (no more check-ins, no longer returned by /me/episode). No-op if it already ends earlier.
    /// </summary>
    [HttpPost("/episodes/{id:guid}/end")]
    [Authorize(Roles = "staff,admin")]
    public async Task<ActionResult<EpisodeListDto>> End(Guid id)
    {
        var ep = await LoadVisible(id);
        if (ep is null) return NotFound();

        var today = AppTime.Today;
        var end = EpisodeStatus.EndNow(ep.StartDate, today);
        if (end < ep.EndDate) ep.EndDate = end;
        await db.SaveChangesAsync();
        return ep.ToListDto(today);
    }

    /// <summary>Move the episode to another doctor group. Staff may only target groups they are active in.</summary>
    [HttpPut("/episodes/{id:guid}/group")]
    [Authorize(Roles = "staff,admin")]
    public async Task<ActionResult<EpisodeListDto>> SetGroup(Guid id, SetEpisodeGroupRequest req)
    {
        var ep = await LoadVisible(id);
        if (ep is null) return NotFound();

        var target = await db.VisibleGroups(User).FirstOrDefaultAsync(g => g.Id == req.DoctorGroupId);
        if (target is null)
            return await db.DoctorGroups.AnyAsync(g => g.Id == req.DoctorGroupId)
                ? Forbid() : Fail(400, "Unknown doctor group.");

        ep.DoctorGroupId = target.Id;
        ep.DoctorGroup = target;
        await db.SaveChangesAsync();
        return ep.ToListDto(AppTime.Today);
    }
}

using Eskrb.Api.Data;
using Eskrb.Api.Domain;
using Eskrb.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Eskrb.Api.Controllers;

public record ProtocolDetailDto(
    string Id, string Name, int DurationDays,
    List<ProtocolQuestion> Questions, List<ProtocolRule> Rules, List<EpisodeListDto> Episodes);
public record GroupMemberDto(Guid UserId, string Name, int Level, string? Title, bool Active);
public record GroupDto(Guid Id, string Name, int EpisodeCount, List<GroupMemberDto> Members);
public record ProtocolListDto(string Id, string Name, int DurationDays, int QuestionCount, int RuleCount, int EpisodeCount);
public record PatientListDto(Guid Id, string Name, int EpisodeCount);
public record StaffDto(Guid Id, string Name, string Role, List<StaffMembershipDto> Memberships);
public record StaffMembershipDto(Guid GroupId, string GroupName, int Level, string? Title, bool Active);
public record NamedRef(string Id, string Name);
public record EpisodeListDto(
    Guid Id, NamedRef Patient, NamedRef Protocol, NamedRef Group,
    DateOnly StartDate, DateOnly EndDate, int DayNumber, string Status);

public static class EpisodeMapping
{
    /// <summary>Requires Patient, Protocol and DoctorGroup to be loaded.</summary>
    public static EpisodeListDto ToListDto(this Episode e, DateOnly today) => new(
        e.Id,
        new NamedRef(e.PatientId.ToString(), e.Patient!.Name),
        new NamedRef(e.ProtocolId, e.Protocol!.Name),
        new NamedRef(e.DoctorGroupId.ToString(), e.DoctorGroup!.Name),
        e.StartDate, e.EndDate, today.DayNumber - e.StartDate.DayNumber + 1,
        EpisodeStatus.Of(e.StartDate, e.EndDate, today));
}

/// <summary>Read-only list views for the dashboard. Staff are scoped to their groups; admins see all.</summary>
[ApiController]
[Authorize(Roles = "staff,admin")]
public class DirectoryController(EskrbDb db) : ControllerBase
{
    private static bool IsActive(DoctorGroupMember m, DateTime now) =>
        (m.ActiveFrom == null || m.ActiveFrom <= now) && (m.ActiveUntil == null || m.ActiveUntil > now);

    [HttpGet("/doctor-groups")]
    public async Task<List<GroupDto>> Groups()
    {
        var now = DateTime.UtcNow;
        var groups = await db.VisibleGroups(User)
            .Include(g => g.Members).ThenInclude(m => m.User)
            .OrderBy(g => g.Name).ToListAsync();
        var counts = await db.VisibleEpisodes(User)
            .GroupBy(e => e.DoctorGroupId).Select(x => new { x.Key, N = x.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.N);

        return groups.Select(g => new GroupDto(
            g.Id, g.Name, counts.GetValueOrDefault(g.Id),
            g.Members.OrderBy(m => m.Level).ThenBy(m => m.User!.Name)
                .Select(m => new GroupMemberDto(m.UserId, m.User!.Name, m.Level, m.Title, IsActive(m, now)))
                .ToList())).ToList();
    }

    /// <summary>Protocols are global; episode counts are limited to what the caller can see.</summary>
    [HttpGet("/protocols")]
    public async Task<List<ProtocolListDto>> Protocols()
    {
        var counts = await db.VisibleEpisodes(User)
            .GroupBy(e => e.ProtocolId).Select(x => new { x.Key, N = x.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.N);
        var protocols = await db.Protocols.OrderBy(p => p.Name).ToListAsync();
        return protocols.Select(p => new ProtocolListDto(
            p.Id, p.Name, p.DurationDays, p.Questions.Count, p.Rules.Count, counts.GetValueOrDefault(p.Id))).ToList();
    }

    /// <summary>Full protocol (questions + rules) and the episodes on it that the caller can see.</summary>
    [HttpGet("/protocols/{id}")]
    public async Task<ActionResult<ProtocolDetailDto>> ProtocolDetail(string id)
    {
        var p = await db.Protocols.FindAsync(id);
        if (p is null) return NotFound();

        var rows = await db.VisibleEpisodes(User).Where(e => e.ProtocolId == id)
            .Include(e => e.Patient).Include(e => e.Protocol).Include(e => e.DoctorGroup)
            .OrderBy(e => e.Patient!.Name).ToListAsync();
        var today = AppTime.Today;
        var episodes = rows.Select(e => e.ToListDto(today)).ToList();

        return new ProtocolDetailDto(p.Id, p.Name, p.DurationDays, p.Questions, p.Rules, episodes);
    }

    [HttpGet("/patients")]
    public async Task<List<PatientListDto>> Patients()
    {
        var counts = await db.VisibleEpisodes(User)
            .GroupBy(e => e.PatientId).Select(x => new { x.Key, N = x.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.N);

        var query = db.Users.Where(u => u.Role == UserRole.Patient);
        if (!User.IsAdmin()) query = query.Where(u => counts.Keys.Contains(u.Id));
        var patients = await query.OrderBy(u => u.Name).ToListAsync();
        return patients.Select(u => new PatientListDto(u.Id, u.Name, counts.GetValueOrDefault(u.Id))).ToList();
    }

    /// <summary>Employees and their group memberships. Admin only.</summary>
    [HttpGet("/staff")]
    [Authorize(Roles = "admin")]
    public async Task<List<StaffDto>> Staff()
    {
        var now = DateTime.UtcNow;
        var users = await db.Users.Where(u => u.Role == UserRole.Staff || u.Role == UserRole.Admin)
            .OrderBy(u => u.Name).ToListAsync();
        var members = await db.DoctorGroupMembers.Include(m => m.Group).ToListAsync();
        return users.Select(u => new StaffDto(
            u.Id, u.Name, u.Role.ToString().ToLowerInvariant(),
            members.Where(m => m.UserId == u.Id)
                .Select(m => new StaffMembershipDto(m.GroupId, m.Group!.Name, m.Level, m.Title, IsActive(m, now)))
                .ToList())).ToList();
    }

    /// <summary>
    /// Episodes the caller can see, optionally filtered. <c>employeeId</c> = episodes of every group that
    /// employee is an active member of. Staff may only pass their own id.
    /// </summary>
    [HttpGet("/episodes")]
    public async Task<ActionResult<List<EpisodeListDto>>> Episodes(
        string? protocolId, Guid? groupId, Guid? employeeId, Guid? patientId)
    {
        if (employeeId is { } emp && !User.IsAdmin() && emp != User.UserId()) return Forbid();

        var q = db.VisibleEpisodes(User);
        if (protocolId is not null) q = q.Where(e => e.ProtocolId == protocolId);
        if (groupId is not null) q = q.Where(e => e.DoctorGroupId == groupId);
        if (patientId is not null) q = q.Where(e => e.PatientId == patientId);
        if (employeeId is { } id)
        {
            var groups = db.ActiveGroupIds(id);
            q = q.Where(e => groups.Contains(e.DoctorGroupId));
        }

        var rows = await q.Include(e => e.Patient).Include(e => e.Protocol).Include(e => e.DoctorGroup)
            .OrderBy(e => e.Patient!.Name).ToListAsync();
        var today = AppTime.Today;
        return rows.Select(e => e.ToListDto(today)).ToList();
    }
}

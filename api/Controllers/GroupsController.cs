using Eskrb.Api.Data;
using Eskrb.Api.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Eskrb.Api.Controllers;

public record CreateGroupRequest(string Name);
public record AddMemberRequest(Guid UserId, int Level = 1, string? Title = null, DateTime? ActiveFrom = null, DateTime? ActiveUntil = null);

/// <summary>Doctor group and membership writes. Admin only.</summary>
[ApiController]
[Authorize(Roles = "admin")]
public class GroupsController(EskrbDb db) : ControllerBase
{
    private ActionResult Fail(int status, string detail) => Problem(detail: detail, statusCode: status);

    [HttpPost("/doctor-groups")]
    public async Task<ActionResult<GroupDto>> Create(CreateGroupRequest req)
    {
        var name = req.Name.Trim();
        if (name.Length == 0) return Fail(400, "name is required.");
        if (await db.DoctorGroups.AnyAsync(g => g.Name.ToLower() == name.ToLower()))
            return Fail(409, $"A group named '{name}' already exists.");

        var g = new DoctorGroup { Id = Guid.NewGuid(), Name = name };
        db.DoctorGroups.Add(g);
        await db.SaveChangesAsync();
        return StatusCode(201, new GroupDto(g.Id, g.Name, 0, []));
    }

    /// <summary>Deletes the group and its memberships. Refused while any episode still belongs to it.</summary>
    [HttpDelete("/doctor-groups/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var g = await db.DoctorGroups.Include(x => x.Members).FirstOrDefaultAsync(x => x.Id == id);
        if (g is null) return NotFound();
        var episodes = await db.Episodes.CountAsync(e => e.DoctorGroupId == id);
        if (episodes > 0) return Fail(409, $"Group still has {episodes} episode(s); move them to another group first.");

        db.DoctorGroups.Remove(g);
        await db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Add a staff user to a group. One membership per user per group (remove it to re-add).</summary>
    [HttpPost("/doctor-groups/{id:guid}/members")]
    public async Task<ActionResult<GroupMemberDto>> AddMember(Guid id, AddMemberRequest req)
    {
        if (!await db.DoctorGroups.AnyAsync(g => g.Id == id)) return NotFound();
        var user = await db.Users.FindAsync(req.UserId);
        if (user is null || user.Role != UserRole.Staff) return Fail(400, "userId must be an existing staff user.");
        if (req.Level < 1) return Fail(400, "level must be >= 1 (1 = first responder).");
        if (req.ActiveFrom is { } from && req.ActiveUntil is { } until && until <= from)
            return Fail(400, "activeUntil must be after activeFrom.");
        if (await db.DoctorGroupMembers.AnyAsync(m => m.GroupId == id && m.UserId == req.UserId))
            return Fail(409, "User is already a member of this group.");

        var m = new DoctorGroupMember
        {
            Id = Guid.NewGuid(), GroupId = id, UserId = user.Id, Level = req.Level,
            Title = string.IsNullOrWhiteSpace(req.Title) ? null : req.Title.Trim(),
            ActiveFrom = req.ActiveFrom?.ToUniversalTime(), ActiveUntil = req.ActiveUntil?.ToUniversalTime(),
        };
        db.DoctorGroupMembers.Add(m);
        await db.SaveChangesAsync();

        var now = DateTime.UtcNow;
        var active = (m.ActiveFrom == null || m.ActiveFrom <= now) && (m.ActiveUntil == null || m.ActiveUntil > now);
        return StatusCode(201, new GroupMemberDto(user.Id, user.Name, m.Level, m.Title, active));
    }

    [HttpDelete("/doctor-groups/{id:guid}/members/{userId:guid}")]
    public async Task<IActionResult> RemoveMember(Guid id, Guid userId)
    {
        var m = await db.DoctorGroupMembers.FirstOrDefaultAsync(x => x.GroupId == id && x.UserId == userId);
        if (m is null) return NotFound();
        db.DoctorGroupMembers.Remove(m);
        await db.SaveChangesAsync();
        return NoContent();
    }
}

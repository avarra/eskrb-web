using Eskrb.Api.Data;
using Eskrb.Api.Domain;
using Eskrb.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Eskrb.Api.Controllers;

public record UserListDto(Guid Id, string Name, string Role);

/// <summary>Per-user admin view: patients get their episodes/protocols, staff get their memberships.</summary>
public record UserDetailDto(
    Guid Id, string Name, string Role,
    List<EpisodeListDto> Episodes, List<StaffMembershipDto> Memberships);

[ApiController]
[Authorize(Roles = "admin")]
public class UsersController(EskrbDb db) : ControllerBase
{
    private static string RoleName(UserRole r) => r.ToString().ToLowerInvariant();

    [HttpGet("/users")]
    public async Task<List<UserListDto>> List(string? role)
    {
        var q = db.Users.AsQueryable();
        if (role is not null && Enum.TryParse<UserRole>(role, true, out var r)) q = q.Where(u => u.Role == r);
        var users = await q.OrderBy(u => u.Role).ThenBy(u => u.Name).ToListAsync();
        return users.Select(u => new UserListDto(u.Id, u.Name, RoleName(u.Role))).ToList();
    }

    [HttpGet("/users/{id:guid}")]
    public async Task<ActionResult<UserDetailDto>> Get(Guid id)
    {
        var user = await db.Users.FindAsync(id);
        if (user is null) return NotFound();

        var episodes = new List<EpisodeListDto>();
        if (user.Role == UserRole.Patient)
        {
            var today = AppTime.Today;
            var rows = await db.Episodes.Where(e => e.PatientId == id)
                .Include(e => e.Patient).Include(e => e.Protocol).Include(e => e.DoctorGroup)
                .OrderByDescending(e => e.StartDate).ToListAsync();
            episodes = rows.Select(e => e.ToListDto(today)).ToList();
        }

        var memberships = new List<StaffMembershipDto>();
        if (user.Role is UserRole.Staff or UserRole.Admin)
        {
            var now = DateTime.UtcNow;
            var rows = await db.DoctorGroupMembers.Where(m => m.UserId == id)
                .Include(m => m.Group).OrderBy(m => m.Group!.Name).ToListAsync();
            memberships = rows.Select(m => new StaffMembershipDto(
                m.GroupId, m.Group!.Name, m.Level, m.Title,
                (m.ActiveFrom == null || m.ActiveFrom <= now) && (m.ActiveUntil == null || m.ActiveUntil > now))).ToList();
        }

        return new UserDetailDto(user.Id, user.Name, RoleName(user.Role), episodes, memberships);
    }
}

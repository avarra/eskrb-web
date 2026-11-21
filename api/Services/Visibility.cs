using System.Security.Claims;
using Eskrb.Api.Data;
using Eskrb.Api.Domain;

namespace Eskrb.Api.Services;

/// <summary>
/// Who sees what. Admins see everything; staff see only groups where they hold an ACTIVE membership,
/// and episodes/patients belonging to those groups.
/// </summary>
public static class Visibility
{
    public static Guid UserId(this ClaimsPrincipal u) => Guid.Parse(u.FindFirstValue(ClaimTypes.NameIdentifier)!);
    public static bool IsAdmin(this ClaimsPrincipal u) => u.IsInRole("admin");

    /// <summary>Group ids the user is an active member of right now.</summary>
    public static IQueryable<Guid> ActiveGroupIds(this EskrbDb db, Guid userId)
    {
        var now = DateTime.UtcNow;
        return db.DoctorGroupMembers
            .Where(m => m.UserId == userId
                && (m.ActiveFrom == null || m.ActiveFrom <= now)
                && (m.ActiveUntil == null || m.ActiveUntil > now))
            .Select(m => m.GroupId);
    }

    public static IQueryable<DoctorGroup> VisibleGroups(this EskrbDb db, ClaimsPrincipal u)
    {
        if (u.IsAdmin()) return db.DoctorGroups;
        var ids = db.ActiveGroupIds(u.UserId());
        return db.DoctorGroups.Where(g => ids.Contains(g.Id));
    }

    public static IQueryable<Episode> VisibleEpisodes(this EskrbDb db, ClaimsPrincipal u)
    {
        if (u.IsAdmin()) return db.Episodes;
        var ids = db.ActiveGroupIds(u.UserId());
        return db.Episodes.Where(e => ids.Contains(e.DoctorGroupId));
    }
}

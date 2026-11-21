using System.Security.Claims;
using Eskrb.Api.Data;
using Eskrb.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Eskrb.Api.Services;

public static class EpisodeAccess
{
    /// <summary>The caller's own episode (with protocol), or null.</summary>
    public static Task<Episode?> OwnEpisode(this EskrbDb db, Guid episodeId, Guid patientId) =>
        db.Episodes.Include(e => e.Protocol).FirstOrDefaultAsync(e => e.Id == episodeId && e.PatientId == patientId);

    public static bool IsActive(this Episode e, DateOnly today) => today >= e.StartDate && today <= e.EndDate;

    public enum Access { None, Patient, Staff }

    /// <summary>
    /// How the caller may reach an episode: the patient who owns it, or staff/admin who can see it
    /// (admin: all; staff: via an active group membership). None otherwise.
    /// </summary>
    public static async Task<(Episode? Episode, Access Access)> ResolveAsync(this EskrbDb db, ClaimsPrincipal user, Guid episodeId)
    {
        if (user.IsInRole("patient"))
        {
            var own = await db.OwnEpisode(episodeId, user.UserId());
            return own is null ? (null, Access.None) : (own, Access.Patient);
        }
        if (user.IsInRole("staff") || user.IsAdmin())
        {
            var ep = await db.VisibleEpisodes(user).Include(e => e.Protocol).FirstOrDefaultAsync(e => e.Id == episodeId);
            return ep is null ? (null, Access.None) : (ep, Access.Staff);
        }
        return (null, Access.None);
    }
}

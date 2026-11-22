using Eskrb.Api.Data;
using Eskrb.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Eskrb.Api.Services;

public static class BuddyAccess
{
    /// <summary>Pairs that are still running: not ended by a person and both episodes not over.</summary>
    public static IQueryable<BuddyPair> Running(this EskrbDb db, DateOnly today) =>
        db.BuddyPairs.Where(p => p.EndedAt == null && p.EpisodeA!.EndDate >= today && p.EpisodeB!.EndDate >= today);

    /// <summary>The patient's most recent pair (running or not), with both episodes loaded.</summary>
    public static Task<BuddyPair?> LatestPairAsync(this EskrbDb db, Guid patientId, CancellationToken ct) =>
        db.BuddyPairs.Include(p => p.EpisodeA).Include(p => p.EpisodeB)
            .Where(p => p.PatientAId == patientId || p.PatientBId == patientId)
            .OrderByDescending(p => p.CreatedAt).FirstOrDefaultAsync(ct);

    public static bool IsRunning(this BuddyPair p, DateOnly today) =>
        p.EndedAt == null && p.EpisodeA!.EndDate >= today && p.EpisodeB!.EndDate >= today;

    public static Guid OtherPatient(this BuddyPair p, Guid me) => p.PatientAId == me ? p.PatientBId : p.PatientAId;
    public static Episode MyEpisode(this BuddyPair p, Guid me) => p.PatientAId == me ? p.EpisodeA! : p.EpisodeB!;
    public static Episode OtherEpisode(this BuddyPair p, Guid me) => p.PatientAId == me ? p.EpisodeB! : p.EpisodeA!;
}

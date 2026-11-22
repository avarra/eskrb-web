using Eskrb.Api.Data;
using Eskrb.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Eskrb.Api.Controllers;

public record PhotoPoint(Guid LogId, string MediaUrl, DateTime At, DateOnly Date, Guid? CheckInId);
public record PhotoSeries(string QuestionId, string Title, List<PhotoPoint> Photos);

/// <summary>All photos of an episode grouped by kind, oldest first, for the "is the wound healing?" timeline.</summary>
[ApiController]
[Authorize(Roles = "patient,staff,admin")]
public class PhotosController(EskrbDb db) : ControllerBase
{
    public const string OtherId = "_other";

    /// <summary>
    /// One series per photo question of the protocol (check-in photos; the answer is the photo's /media URL), plus
    /// <c>_other</c> for loose photo notes. Only series with photos are returned. Fetch images with the bearer token.
    /// </summary>
    [HttpGet("/episodes/{id:guid}/photos")]
    public async Task<ActionResult<List<PhotoSeries>>> Get(Guid id, CancellationToken ct)
    {
        var (ep, _) = await db.ResolveAsync(User, id);
        if (ep?.Protocol is null) return NotFound();

        var logs = (await db.Logs.Where(l => l.EpisodeId == id && l.Type == "photo" && l.MediaKey != null).ToListAsync(ct)).ToDictionary(l => l.Id);
        var checkIns = await db.CheckIns.Where(c => c.EpisodeId == id).ToListAsync(ct);
        var used = new HashSet<Guid>();
        var result = new List<PhotoSeries>();

        foreach (var q in ep.Protocol.Questions.Where(q => q.Type == "photo"))
        {
            var points = new List<PhotoPoint>();
            foreach (var c in checkIns)
            {
                if (!c.Answers.TryGetValue(q.Id, out var a) || a.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                var url = a.GetString()!;
                if (!url.StartsWith("/media/") || !Guid.TryParse(url["/media/".Length..], out var logId) || !logs.TryGetValue(logId, out var log)) continue;
                used.Add(logId);
                points.Add(new PhotoPoint(logId, url, log.CreatedAt, AppTime.ToLocalDate(log.CreatedAt), c.Id));
            }
            if (points.Count > 0) result.Add(new PhotoSeries(q.Id, q.Text, points.OrderBy(p => p.At).ToList()));
        }

        var loose = logs.Values.Where(l => !used.Contains(l.Id)).OrderBy(l => l.CreatedAt)
            .Select(l => new PhotoPoint(l.Id, $"/media/{l.Id}", l.CreatedAt, AppTime.ToLocalDate(l.CreatedAt), null)).ToList();
        if (loose.Count > 0) result.Add(new PhotoSeries(OtherId, "Other photos", loose));
        return result;
    }
}

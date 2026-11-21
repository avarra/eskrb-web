using Eskrb.Api.Data;
using Eskrb.Api.Services;
using Eskrb.Api.Services.Media;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Eskrb.Api.Controllers;

/// <summary>Authorized photo access. Photos are never public static files. The URL is opaque (the log id).</summary>
[ApiController]
[Authorize]
public class MediaController(EskrbDb db, IMediaStore media) : ControllerBase
{
    [HttpGet("/media/{logId:guid}")]
    public async Task<IActionResult> Get(Guid logId)
    {
        var log = await db.Logs.FirstOrDefaultAsync(l => l.Id == logId && l.MediaKey != null);
        if (log is null) return NotFound();

        var (episode, _) = await db.ResolveAsync(User, log.EpisodeId);
        if (episode is null) return NotFound();

        try
        {
            Response.Headers.XContentTypeOptions = "nosniff";
            Response.Headers.CacheControl = "private, max-age=3600";
            return File(media.Open(log.MediaKey!), log.MediaContentType ?? "application/octet-stream");
        }
        catch (FileNotFoundException) { return NotFound(); }
    }
}

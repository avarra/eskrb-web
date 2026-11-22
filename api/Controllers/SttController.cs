using Eskrb.Api.Data;
using Eskrb.Api.Integrations;
using Eskrb.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Eskrb.Api.Controllers;

public record TemporaryKeyDto(string ApiKey, DateTimeOffset ExpiresAt);

/// <summary>
/// Speech-to-text, the only part we play: hand the phone a short-lived Soniox key so audio streams phone -> Soniox
/// and never reaches this server. Logs only episode id, time and outcome (never keys or bodies).
/// </summary>
[ApiController]
[Authorize(Roles = "patient")]
public class SttController(EskrbDb db, ISttKeyIssuer issuer, ILogger<SttController> logger) : ControllerBase
{
    /// <summary>Single-use key, valid ~15 min (`Soniox:KeyTtlSeconds`) to open one stream that may run as long. Tied to the caller's active episode.</summary>
    [HttpPost("/stt/temporary-key")]
    [EnableRateLimiting("stt")]
    public async Task<ActionResult<TemporaryKeyDto>> Issue(CancellationToken ct)
    {
        var userId = User.UserId();
        var today = AppTime.Today;
        var episode = await db.Episodes.Where(e => e.PatientId == userId && e.StartDate <= today && e.EndDate >= today)
            .Select(e => e.Id).FirstOrDefaultAsync(ct);
        if (episode == Guid.Empty) return NotFound();

        try
        {
            var key = await issuer.IssueAsync(episode.ToString(), ct);
            logger.LogInformation("STT temporary key issued for episode {EpisodeId}", episode);
            return new TemporaryKeyDto(key.ApiKey, key.ExpiresAt);
        }
        catch (SttNotConfiguredException)
        {
            logger.LogWarning("STT temporary key failed for episode {EpisodeId}: Soniox:ApiKey not configured", episode);
            return Problem(detail: "Speech-to-text is not configured on this server.", statusCode: 503);
        }
        catch (SttUpstreamException ex)
        {
            logger.LogWarning("STT temporary key failed for episode {EpisodeId}: {Reason}", episode, ex.Message);
            return Problem(detail: "Could not get a speech-to-text key right now. Try again shortly.", statusCode: 502);
        }
    }
}

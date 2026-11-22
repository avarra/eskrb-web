using System.Security.Claims;
using System.Text.Json;
using Eskrb.Api.Data;
using Eskrb.Api.Domain;
using Eskrb.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Eskrb.Api.Controllers;

public record MeDto(Guid Id, string Name, string Role);

public record ProtocolDto(
    string Id, string Name, int DurationDays,
    List<ProtocolQuestion> Questions, List<ProtocolRule> Rules);

public record EpisodeDto(
    Guid Id, DateOnly StartDate, DateOnly EndDate, int DayNumber, bool CheckInDoneToday, ProtocolDto Protocol,
    int UnreadMessages, List<NoteDto> Notes);

[ApiController]
[Authorize]
public class MeController(EskrbDb db) : ControllerBase
{
    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("/me")]
    public async Task<ActionResult<MeDto>> Get()
    {
        var user = await db.Users.FindAsync(UserId);
        if (user is null) return Unauthorized();
        return new MeDto(user.Id, user.Name, user.Role.ToString().ToLowerInvariant());
    }

    /// <summary>The patient's active episode (today within its dates) with its protocol and day number; 404 if none.</summary>
    [HttpGet("/me/episode")]
    [Authorize(Roles = "patient")]
    public async Task<ActionResult<EpisodeDto>> GetEpisode()
    {
        var today = AppTime.Today;
        var ep = await db.Episodes
            .Include(e => e.Protocol)
            .Where(e => e.PatientId == UserId && e.StartDate <= today && e.EndDate >= today)
            .OrderByDescending(e => e.StartDate)
            .FirstOrDefaultAsync();
        if (ep?.Protocol is null) return NotFound();

        var notes = (await db.EpisodeNotes.Where(n => n.EpisodeId == ep.Id && n.Kind != NoteKinds.Private).ToListAsync())
            .OrderBy(n => n.CreatedAt).Select(NotesController.ToDto).ToList();
        var p = ep.Protocol;
        var done = await db.CheckIns.AnyAsync(c => c.EpisodeId == ep.Id && c.Date == today);
        var readAt = (await db.EpisodeReadStates.FindAsync(ep.Id))?.PatientLastReadAt ?? DateTime.MinValue;
        var unread = await db.Messages.CountAsync(m => m.EpisodeId == ep.Id && m.FromUserId != UserId && m.CreatedAt > readAt);
        return new EpisodeDto(
            ep.Id, ep.StartDate, ep.EndDate,
            today.DayNumber - ep.StartDate.DayNumber + 1, done,
            new ProtocolDto(p.Id, p.Name, p.DurationDays, p.Questions, p.Rules), unread, notes);
    }
}

using System.Text.Json;
using Eskrb.Api.Data;
using Eskrb.Api.Domain;
using Eskrb.Api.Rules;
using Eskrb.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Eskrb.Api.Controllers;

public record CheckInRequest(Dictionary<string, JsonElement> Answers);
public record FlagDto(Guid Id, string RuleId, string Severity, string Quote);
public record CheckInResultDto(
    Guid Id, DateOnly Date, Dictionary<string, JsonElement> Answers, List<FlagDto> Flags, string PatientAction);

[ApiController]
public class CheckInsController(EskrbDb db) : ControllerBase
{
    /// <summary>
    /// Submit a daily check-in for the caller's own episode. Answers are validated against the protocol,
    /// the rules engine (not an LLM) raises flags, and <c>patientAction</c> tells the phone which screen to show.
    /// Several check-ins per day are allowed; the date is set by the server (Europe/Ljubljana).
    /// </summary>
    [HttpPost("/episodes/{id:guid}/checkins")]
    [Authorize(Roles = "patient")]
    public async Task<ActionResult<CheckInResultDto>> Submit(Guid id, CheckInRequest req)
    {
        var userId = User.UserId();
        var ep = await db.Episodes.Include(e => e.Protocol)
            .FirstOrDefaultAsync(e => e.Id == id && e.PatientId == userId);
        if (ep?.Protocol is null) return NotFound();

        var today = AppTime.Today;
        if (today < ep.StartDate || today > ep.EndDate)
            return Problem(detail: "This episode is not active today.", statusCode: 409);

        var errors = AnswerValidator.Validate(ep.Protocol, req.Answers);
        if (errors.Count > 0) return Problem(detail: string.Join(" ", errors), statusCode: 400);

        var now = DateTime.UtcNow;
        var checkIn = new CheckIn
        {
            Id = Guid.NewGuid(), EpisodeId = ep.Id, Date = today,
            Answers = req.Answers, AuthoredBy = userId, CreatedAt = now,
        };
        var flags = RulesEngine.Evaluate(req.Answers, ep.Protocol.Rules)
            .Select(h => new Flag
            {
                Id = Guid.NewGuid(), EpisodeId = ep.Id, SourceType = "checkin", SourceId = checkIn.Id,
                RuleId = h.RuleId, Severity = h.Severity, Quote = $"{h.Field}: {Render(h.Answer)}", CreatedAt = now,
            }).ToList();

        db.CheckIns.Add(checkIn);
        db.Flags.AddRange(flags);
        await db.SaveChangesAsync();

        return StatusCode(201, new CheckInResultDto(
            checkIn.Id, checkIn.Date, checkIn.Answers,
            flags.Select(f => new FlagDto(f.Id, f.RuleId, f.Severity, f.Quote)).ToList(),
            PatientActions.From(flags.Select(f => f.Severity))));
    }

    private static string Render(JsonElement a) => a.ValueKind switch
    {
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => a.ToString(),
    };
}

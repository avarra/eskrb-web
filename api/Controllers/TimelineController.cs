using System.Text.Json;
using Eskrb.Api.Data;
using Eskrb.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Eskrb.Api.Controllers;

/// <summary>One entry of an episode's history. Fields besides kind/id/at depend on the kind.</summary>
public record TimelineItem(
    string Kind,                                   // checkin | log | message
    Guid Id,
    DateTime At,
    bool? Reviewed,                                // checkin, log
    Dictionary<string, JsonElement>? Answers,      // checkin
    string? LogType, string? Source, string? Text, string? MediaUrl,   // log
    Dictionary<string, JsonElement>? Extracted,    // log (staff only)
    string? FromName, string? FromRole, string? Body,                  // message
    List<FlagDto>? Flags);                         // checkin/log, staff only

[ApiController]
[Authorize(Roles = "patient,staff,admin")]
public class TimelineController(EskrbDb db) : ControllerBase
{
    /// <summary>
    /// Check-ins, logs and messages of an episode, newest first. The patient gets their own episode;
    /// staff get episodes they can see, and additionally see flags and extracted values.
    /// </summary>
    [HttpGet("/episodes/{id:guid}/timeline")]
    public async Task<ActionResult<List<TimelineItem>>> Get(Guid id, int limit = 100)
    {
        var (ep, access) = await db.ResolveAsync(User, id);
        if (ep is null) return NotFound();
        limit = Math.Clamp(limit, 1, 500);
        var staff = access == EpisodeAccess.Access.Staff;

        var flags = staff
            ? (await db.Flags.Where(f => f.EpisodeId == id).ToListAsync())
                .GroupBy(f => f.SourceId).ToDictionary(g => g.Key, g => g.Select(f => new FlagDto(f.Id, f.RuleId, f.Severity, f.Quote)).ToList())
            : [];
        List<FlagDto>? FlagsOf(Guid sourceId) => staff ? flags.GetValueOrDefault(sourceId) ?? [] : null;

        var items = new List<TimelineItem>();
        var checkIns = await db.CheckIns.Where(c => c.EpisodeId == id).ToListAsync();
        // Photos attached to a check-in question appear inside that check-in (answer = "/media/<logId>"), not as separate notes.
        var inCheckIn = checkIns.SelectMany(c => c.Answers.Values)
            .Where(v => v.ValueKind == JsonValueKind.String && v.GetString()!.StartsWith("/media/"))
            .Select(v => v.GetString()!).ToHashSet();
        foreach (var c in checkIns)
            items.Add(new("checkin", c.Id, c.CreatedAt, c.ReviewedAt != null, c.Answers, null, null, null, null, null, null, null, null, FlagsOf(c.Id)));
        foreach (var l in await db.Logs.Where(l => l.EpisodeId == id).ToListAsync())
        {
            if (inCheckIn.Contains($"/media/{l.Id}")) continue;
            items.Add(new("log", l.Id, l.CreatedAt, l.ReviewedAt != null, null, l.Type, l.Source, l.Text,
                l.MediaKey is null ? null : $"/media/{l.Id}", staff ? l.Extracted : null, null, null, null, FlagsOf(l.Id)));
        }
        foreach (var m in await db.Messages.Where(m => m.EpisodeId == id).Include(m => m.FromUser).ToListAsync())
            items.Add(new("message", m.Id, m.CreatedAt, null, null, null, null, null, null, null,
                m.FromUser!.Name, m.FromUser.Role.ToString().ToLowerInvariant(), m.Body, null));

        return items.OrderByDescending(i => i.At).Take(limit).ToList();
    }
}

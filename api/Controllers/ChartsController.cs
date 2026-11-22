using System.Text.Json;
using Eskrb.Api.Data;
using Eskrb.Api.Domain;
using Eskrb.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Eskrb.Api.Controllers;

public record ChartRule(string Op, double Value, string Severity);
public record ChartPoint(DateTime At, DateOnly Date, double Value, string Source);
public record ChartVariable(string QuestionId, string Text, string Type, string? Unit, int? Min, int? Max, List<ChartRule> Rules, List<ChartPoint> Points);
public record ActivityDay(DateOnly Date, int CheckIns, int Notes, int Photos, int PatientMessages, int StaffMessages, int Attention, int Urgent);
public record EpisodeCharts(DateOnly StartDate, DateOnly EndDate, DateOnly Today, int DayNumber, List<ChartVariable> Variables, List<ActivityDay> Days);

/// <summary>Staff view of how an episode is going over time: numeric variables with their rule thresholds, and per-day activity.</summary>
[ApiController]
[Authorize(Roles = "staff,admin")]
public class ChartsController(EskrbDb db) : ControllerBase
{
    /// <summary>
    /// <c>variables</c>: every number/scale question with its readings (check-in answers and values the LLM extracted from
    /// notes) and the protocol rules on it (so the dashboard can shade attention/urgent zones). <c>days</c>: one entry per
    /// day from the start to today with counts of check-ins, notes, photos, messages and flags raised.
    /// </summary>
    [HttpGet("/episodes/{id:guid}/charts")]
    public async Task<ActionResult<EpisodeCharts>> Get(Guid id, CancellationToken ct)
    {
        var (ep, _) = await db.ResolveAsync(User, id);
        if (ep?.Protocol is null) return NotFound();
        var today = AppTime.Today;
        var last = today < ep.EndDate ? today : ep.EndDate;

        var checkIns = await db.CheckIns.Where(c => c.EpisodeId == id).ToListAsync(ct);
        var logs = await db.Logs.Where(l => l.EpisodeId == id).ToListAsync(ct);
        var messages = await db.Messages.Where(m => m.EpisodeId == id).Include(m => m.FromUser).ToListAsync(ct);
        var flags = await db.Flags.Where(f => f.EpisodeId == id).ToListAsync(ct);

        var variables = new List<ChartVariable>();
        foreach (var q in ep.Protocol.Questions.Where(q => q.Type is "number" or "scale"))
        {
            var points = new List<ChartPoint>();
            foreach (var c in checkIns)
                if (c.Answers.TryGetValue(q.Id, out var v) && v.ValueKind == JsonValueKind.Number)
                    points.Add(new ChartPoint(c.CreatedAt, c.Date, v.GetDouble(), "checkin"));
            foreach (var l in logs)
                if (l.Extracted.TryGetValue(q.Id, out var v) && v.ValueKind == JsonValueKind.Number)
                    points.Add(new ChartPoint(l.CreatedAt, AppTime.ToLocalDate(l.CreatedAt), v.GetDouble(), "note"));
            if (points.Count == 0) continue;

            var rules = ep.Protocol.Rules
                .Where(r => r.Field == q.Id && r.Value.ValueKind == JsonValueKind.Number && r.Op is ">" or ">=" or "<" or "<=")
                .Select(r => new ChartRule(r.Op, r.Value.GetDouble(), r.Severity)).ToList();
            variables.Add(new ChartVariable(q.Id, q.Text, q.Type, q.Unit, q.Min, q.Max, rules, points.OrderBy(p => p.At).ToList()));
        }

        var days = new List<ActivityDay>();
        for (var d = ep.StartDate; d <= last; d = d.AddDays(1))
        {
            var dayLogs = logs.Where(l => AppTime.ToLocalDate(l.CreatedAt) == d).ToList();
            var dayMsgs = messages.Where(m => AppTime.ToLocalDate(m.CreatedAt) == d).ToList();
            var dayFlags = flags.Where(f => AppTime.ToLocalDate(f.CreatedAt) == d).ToList();
            days.Add(new ActivityDay(d,
                checkIns.Count(c => c.Date == d),
                dayLogs.Count(l => l.Type == "text"),
                dayLogs.Count(l => l.Type == "photo"),
                dayMsgs.Count(m => m.FromUser!.Role == UserRole.Patient),
                dayMsgs.Count(m => m.FromUser!.Role != UserRole.Patient),
                dayFlags.Count(f => f.Severity == "attention"),
                dayFlags.Count(f => f.Severity == "urgent")));
        }

        return new EpisodeCharts(ep.StartDate, ep.EndDate, today, Math.Max(1, today.DayNumber - ep.StartDate.DayNumber + 1), variables, days);
    }
}

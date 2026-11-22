using Eskrb.Api.Data;
using Eskrb.Api.Domain;
using Eskrb.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Eskrb.Api.Controllers;

public record NoteDto(Guid Id, string Kind, string? Title, string Body, DateTime CreatedAt, DateTime UpdatedAt);
public record NoteRequest(string? Kind, string? Title, string? Body);

/// <summary>
/// Notes attached to an episode: <c>medication</c> (instructions; makes the check-in ask about meds), <c>advice</c>,
/// and <c>private</c> (staff only). Patients only ever receive the first two, read-only.
/// </summary>
[ApiController]
[Authorize(Roles = "patient,staff,admin")]
public class NotesController(EskrbDb db) : ControllerBase
{
    public static NoteDto ToDto(EpisodeNote n) => new(n.Id, n.Kind, n.Title, n.Body, n.CreatedAt, n.UpdatedAt);

    /// <summary>Patient: their own episode's medication and advice notes. Staff/admin: all notes of a visible episode, incl. private.</summary>
    [HttpGet("/episodes/{id:guid}/notes")]
    public async Task<ActionResult<List<NoteDto>>> List(Guid id, CancellationToken ct)
    {
        var (ep, access) = await db.ResolveAsync(User, id);
        if (ep is null) return NotFound();
        var q = db.EpisodeNotes.Where(n => n.EpisodeId == id);
        if (access == EpisodeAccess.Access.Patient) q = q.Where(n => n.Kind != NoteKinds.Private);
        return (await q.ToListAsync(ct)).OrderBy(n => n.CreatedAt).Select(ToDto).ToList();
    }

    [HttpPost("/episodes/{id:guid}/notes")]
    [Authorize(Roles = "staff,admin")]
    public async Task<ActionResult<NoteDto>> Create(Guid id, NoteRequest req, CancellationToken ct)
    {
        var (ep, _) = await db.ResolveAsync(User, id);
        if (ep is null) return NotFound();
        if (Validate(req, creating: true) is { } error) return Problem(detail: error, statusCode: 400);

        var now = DateTime.UtcNow;
        var note = new EpisodeNote
        {
            Id = Guid.NewGuid(), EpisodeId = id, Kind = req.Kind!, Title = Clean(req.Title), Body = req.Body!.Trim(),
            CreatedBy = User.UserId(), UpdatedBy = User.UserId(), CreatedAt = now, UpdatedAt = now,
        };
        db.EpisodeNotes.Add(note);
        await db.SaveChangesAsync(ct);
        return StatusCode(201, ToDto(note));
    }

    /// <summary>Edit any of kind/title/body (only the fields sent change; an empty title clears it).</summary>
    [HttpPatch("/episodes/{id:guid}/notes/{noteId:guid}")]
    [Authorize(Roles = "staff,admin")]
    public async Task<ActionResult<NoteDto>> Update(Guid id, Guid noteId, NoteRequest req, CancellationToken ct)
    {
        var (ep, _) = await db.ResolveAsync(User, id);
        if (ep is null) return NotFound();
        var note = await db.EpisodeNotes.FirstOrDefaultAsync(n => n.Id == noteId && n.EpisodeId == id, ct);
        if (note is null) return NotFound();
        if (Validate(req, creating: false) is { } error) return Problem(detail: error, statusCode: 400);

        if (req.Kind is not null) note.Kind = req.Kind;
        if (req.Title is not null) note.Title = Clean(req.Title);
        if (req.Body is not null) note.Body = req.Body.Trim();
        note.UpdatedBy = User.UserId();
        note.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return ToDto(note);
    }

    [HttpDelete("/episodes/{id:guid}/notes/{noteId:guid}")]
    [Authorize(Roles = "staff,admin")]
    public async Task<IActionResult> Delete(Guid id, Guid noteId, CancellationToken ct)
    {
        var (ep, _) = await db.ResolveAsync(User, id);
        if (ep is null) return NotFound();
        var note = await db.EpisodeNotes.FirstOrDefaultAsync(n => n.Id == noteId && n.EpisodeId == id, ct);
        if (note is null) return NotFound();
        db.EpisodeNotes.Remove(note);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? Validate(NoteRequest r, bool creating)
    {
        if (creating && r.Kind is null) return $"kind is required ({string.Join(", ", NoteKinds.All)}).";
        if (r.Kind is not null && !NoteKinds.All.Contains(r.Kind)) return $"kind must be one of: {string.Join(", ", NoteKinds.All)}.";
        if (creating && string.IsNullOrWhiteSpace(r.Body)) return "body is required.";
        if (r.Body is not null && (r.Body.Trim().Length == 0 || r.Body.Length > 2000)) return "body must be 1-2000 characters.";
        if (r.Title is { Length: > 100 }) return "title is longer than 100 characters.";
        return null;
    }
}

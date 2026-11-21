using Eskrb.Api.Data;
using Eskrb.Api.Domain;
using Eskrb.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Eskrb.Api.Controllers;

public record SendMessageRequest(string Body);
public record MessageDto(Guid Id, Guid FromUserId, string FromName, string FromRole, string Body, DateTime CreatedAt);

[ApiController]
[Authorize(Roles = "patient,staff,admin")]
public class MessagesController(EskrbDb db) : ControllerBase
{
    private const int MaxLength = 2000;

    /// <summary>
    /// Staff -> patient (episodes they can see) or patient -> staff (own episode, while it is active).
    /// </summary>
    [HttpPost("/episodes/{id:guid}/messages")]
    public async Task<ActionResult<MessageDto>> Send(Guid id, SendMessageRequest req)
    {
        var (ep, access) = await db.ResolveAsync(User, id);
        if (ep is null) return NotFound();
        if (access == EpisodeAccess.Access.Patient && !ep.IsActive(AppTime.Today))
            return Problem(detail: "This episode is not active today.", statusCode: 409);

        var body = req.Body?.Trim() ?? "";
        if (body.Length == 0) return Problem(detail: "body is required.", statusCode: 400);
        if (body.Length > MaxLength) return Problem(detail: $"body is longer than {MaxLength} characters.", statusCode: 400);

        var sender = (await db.Users.FindAsync(User.UserId()))!;
        var m = new Message { Id = Guid.NewGuid(), EpisodeId = ep.Id, FromUserId = sender.Id, Body = body, CreatedAt = DateTime.UtcNow };
        db.Messages.Add(m);
        await db.SaveChangesAsync();
        return StatusCode(201, new MessageDto(m.Id, sender.Id, sender.Name, sender.Role.ToString().ToLowerInvariant(), m.Body, m.CreatedAt));
    }
}

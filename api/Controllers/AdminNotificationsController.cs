using Eskrb.Api.Data;
using Eskrb.Api.Domain;
using Eskrb.Api.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Eskrb.Api.Controllers;

public record SendNotificationRequest(string? Audience, Guid? UserId, string? Title, string? Body);
public record PushDeviceDto(string Platform, DateTime RegisteredAt);
public record PushUserDto(Guid UserId, string Name, string Role, List<PushDeviceDto> Devices);

/// <summary>Admin tool: see who can receive pushes and send a manual notification (test, announcement).</summary>
[ApiController]
[Authorize(Roles = "admin")]
public class AdminNotificationsController(EskrbDb db, INotificationService notifications, ILogger<AdminNotificationsController> logger) : ControllerBase
{
    /// <summary>Users with at least one registered device (never returns the tokens themselves).</summary>
    [HttpGet("/admin/push-devices")]
    public async Task<List<PushUserDto>> Devices(CancellationToken ct) =>
        (await db.PushDevices.Include(d => d.User).ToListAsync(ct))
            .GroupBy(d => d.User!)
            .OrderBy(g => g.Key.Name)
            .Select(g => new PushUserDto(g.Key.Id, g.Key.Name, g.Key.Role.ToString().ToLowerInvariant(),
                g.OrderBy(d => d.CreatedAt).Select(d => new PushDeviceDto(d.Platform, d.CreatedAt)).ToList()))
            .ToList();

    /// <summary>
    /// Send now. <c>audience</c>: <c>user</c> (needs userId), <c>patients</c> or <c>staff</c> (everyone with that role).
    /// Remember lock screens are public: keep it generic. Returns how many devices were reached.
    /// </summary>
    [HttpPost("/admin/notifications")]
    public async Task<ActionResult<NotifyResult>> Send(SendNotificationRequest req, CancellationToken ct)
    {
        var title = req.Title?.Trim() ?? "";
        var body = req.Body?.Trim() ?? "";
        if (title.Length is 0 or > 100) return Problem(detail: "title must be 1-100 characters.", statusCode: 400);
        if (body.Length is 0 or > 500) return Problem(detail: "body must be 1-500 characters.", statusCode: 400);

        List<Guid> ids;
        switch (req.Audience)
        {
            case "user":
                if (req.UserId is not { } uid || !await db.Users.AnyAsync(u => u.Id == uid, ct))
                    return Problem(detail: "userId must be an existing user.", statusCode: 400);
                ids = [uid];
                break;
            case "patients":
                ids = await db.Users.Where(u => u.Role == UserRole.Patient).Select(u => u.Id).ToListAsync(ct);
                break;
            case "staff":
                ids = await db.Users.Where(u => u.Role == UserRole.Staff || u.Role == UserRole.Admin).Select(u => u.Id).ToListAsync(ct);
                break;
            default:
                return Problem(detail: "audience must be one of: user, patients, staff.", statusCode: 400);
        }

        var result = await notifications.SendToUsersAsync(ids, new PushMessage(title, body, new() { ["kind"] = "manual" }), ct);
        logger.LogInformation("Manual push to {Audience}: {Attempted} devices, {Accepted} accepted", req.Audience, result.Attempted, result.Accepted);
        return result;
    }
}

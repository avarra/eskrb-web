using Eskrb.Api.Data;
using Eskrb.Api.Domain;
using Eskrb.Api.Integrations;
using Eskrb.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Eskrb.Api.Controllers;

public record RegisterPushRequest(string? ExpoToken, string? Platform, string? DeviceId);
public record UnregisterPushRequest(string? ExpoToken);

/// <summary>Device registration for push notifications (the phone calls this after it gets its Expo push token).</summary>
[ApiController]
[Authorize]
public class PushController(EskrbDb db) : ControllerBase
{
    /// <summary>
    /// Register (or refresh) this device for the caller. Idempotent: the same token again just updates the row, and a
    /// token that was registered under another user moves to the caller (shared phone / re-login). 204 on success.
    /// </summary>
    [HttpPost("/me/push-token")]
    public async Task<IActionResult> Register(RegisterPushRequest req, CancellationToken ct)
    {
        if (!ExpoPushService.IsExpoPushToken(req.ExpoToken))
            return Problem(detail: "expoToken must look like ExponentPushToken[...].", statusCode: 400);
        if (req.Platform is not ("ios" or "android"))
            return Problem(detail: "platform must be ios or android.", statusCode: 400);
        if (req.DeviceId is { Length: > 200 }) return Problem(detail: "deviceId is too long.", statusCode: 400);

        var now = DateTime.UtcNow;
        var device = await db.PushDevices.FirstOrDefaultAsync(d => d.ExpoToken == req.ExpoToken, ct);
        if (device is null)
            db.PushDevices.Add(new PushDevice
            {
                Id = Guid.NewGuid(), UserId = User.UserId(), ExpoToken = req.ExpoToken!, Platform = req.Platform,
                DeviceId = req.DeviceId, CreatedAt = now, UpdatedAt = now,
            });
        else
        {
            device.UserId = User.UserId();
            device.Platform = req.Platform;
            device.DeviceId = req.DeviceId;
            device.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Stop pushing to this device for the caller (call on sign-out). Unknown tokens are fine: 204 either way.</summary>
    [HttpDelete("/me/push-token")]
    public async Task<IActionResult> Unregister(UnregisterPushRequest req, CancellationToken ct)
    {
        var me = User.UserId();
        var device = await db.PushDevices.FirstOrDefaultAsync(d => d.ExpoToken == req.ExpoToken && d.UserId == me, ct);
        if (device is not null)
        {
            db.PushDevices.Remove(device);
            await db.SaveChangesAsync(ct);
        }
        return NoContent();
    }
}

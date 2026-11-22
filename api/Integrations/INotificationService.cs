namespace Eskrb.Api.Integrations;

/// <summary>
/// One push notification. Lock screens are public: keep title/body GENERIC ("You have a new message"), never health
/// details or names; put ids (episodeId, kind) in <see cref="Data"/> so the app can deep-link after authenticating.
/// </summary>
public record PushMessage(string Title, string Body, Dictionary<string, object?>? Data = null, int? Badge = null);

/// <param name="Attempted">Valid device tokens we tried to reach.</param>
/// <param name="Accepted">Tickets Expo accepted ("ok" only means Expo got it, not the phone).</param>
/// <param name="Removed">Devices dropped because Expo said DeviceNotRegistered.</param>
/// <param name="Disabled">True when the <c>Notifications:Enabled=false</c> kill switch is on: nothing was sent.</param>
/// <param name="TicketIds">Receipt ids, for <see cref="INotificationService.GetReceiptsAsync"/> ~15 min later.</param>
public record NotifyResult(int Attempted, int Accepted, int Removed, bool Disabled, List<string> TicketIds);

/// <summary>A delivery receipt: status "ok" or "error" (with the error code, e.g. DeviceNotRegistered, MessageRateExceeded).</summary>
public record PushReceipt(string Status, string? Error, string? Message);

/// <summary>
/// Sends push notifications to a user's registered devices. Always behind this interface so tests and the demo can fake it.
/// No automatic triggers yet (only the admin "send notification" screen); wire them deliberately.
/// </summary>
public interface INotificationService
{
    Task<NotifyResult> SendToUserAsync(Guid userId, PushMessage message, CancellationToken ct = default);
    Task<NotifyResult> SendToUsersAsync(IEnumerable<Guid> userIds, PushMessage message, CancellationToken ct = default);
    /// <summary>Fetch receipts for earlier ticket ids (max 1000 per call; receipts live 24 h; ask ~15 min after sending).</summary>
    Task<Dictionary<string, PushReceipt>> GetReceiptsAsync(IReadOnlyCollection<string> ticketIds, CancellationToken ct = default);
}

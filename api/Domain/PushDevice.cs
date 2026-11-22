namespace Eskrb.Api.Domain;

/// <summary>A phone (or browser) that can receive Expo push notifications for a user. One row per Expo token.</summary>
public class PushDevice
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User? User { get; set; }
    /// <summary>ExponentPushToken[...]; unique. If the same phone signs in as someone else, the row moves to them.</summary>
    public required string ExpoToken { get; set; }
    /// <summary>ios | android</summary>
    public required string Platform { get; set; }
    public string? DeviceId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

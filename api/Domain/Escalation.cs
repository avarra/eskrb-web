namespace Eskrb.Api.Domain;

/// <summary>
/// A staff member handing an item (check-in, note or message) of an episode up to the next level of the doctor group,
/// with an optional comment. While open it counts as "needs attention" for staff at <see cref="ToLevel"/> or above.
/// </summary>
public class Escalation
{
    public Guid Id { get; set; }
    public Guid EpisodeId { get; set; }
    public Episode? Episode { get; set; }
    /// <summary>checkin | log | message | episode (the whole episode; SourceId = EpisodeId)</summary>
    public required string SourceType { get; set; }
    public Guid SourceId { get; set; }
    public Guid FromUserId { get; set; }
    public User? FromUser { get; set; }
    /// <summary>Lowest active group level above the sender's, at the time of escalation.</summary>
    public int ToLevel { get; set; }
    public string? Comment { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public Guid? ResolvedBy { get; set; }
}

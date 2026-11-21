namespace Eskrb.Api.Domain;

/// <summary>A message between staff and the patient on an episode.</summary>
public class Message
{
    public Guid Id { get; set; }
    public Guid EpisodeId { get; set; }
    public Episode? Episode { get; set; }
    public Guid FromUserId { get; set; }
    public User? FromUser { get; set; }
    public required string Body { get; set; }
    public DateTime CreatedAt { get; set; }
}

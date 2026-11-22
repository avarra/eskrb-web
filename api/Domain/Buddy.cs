namespace Eskrb.Api.Domain;

/// <summary>Two patients staff paired as recovery buddies ("twins"). Created manually by staff; consent is confirmed at creation.</summary>
public class BuddyPair
{
    public Guid Id { get; set; }
    public Guid EpisodeAId { get; set; }
    public Episode? EpisodeA { get; set; }
    public Guid EpisodeBId { get; set; }
    public Episode? EpisodeB { get; set; }
    public Guid PatientAId { get; set; }
    public Guid PatientBId { get; set; }
    /// <summary>The staff member who paired them (and confirmed both patients agreed).</summary>
    public Guid CreatedBy { get; set; }
    public User? Creator { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public Guid? EndedBy { get; set; }
    /// <summary>left (a patient left) | staff. Never shown to the other patient.</summary>
    public string? EndReason { get; set; }
}

/// <summary>One cheer (kind "cheer", text = its catalogue copy) or free text (typed or dictated on the phone, never audio) between buddies.</summary>
public class BuddyMessage
{
    public Guid Id { get; set; }
    public Guid PairId { get; set; }
    public BuddyPair? Pair { get; set; }
    public Guid FromUserId { get; set; }
    /// <summary>cheer | text</summary>
    public required string Kind { get; set; }
    public string? CheerId { get; set; }
    public required string Text { get; set; }
    /// <summary>typed | dictated (text only)</summary>
    public string? Source { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ReadAt { get; set; }
    public DateTime? ReportedAt { get; set; }
    /// <summary>Set by staff moderation; hidden messages are never returned to patients.</summary>
    public DateTime? HiddenAt { get; set; }
}

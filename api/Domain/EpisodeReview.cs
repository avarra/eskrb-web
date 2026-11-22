namespace Eskrb.Api.Domain;

public static class ReviewStatuses
{
    public const string NoAction = "no_action";
    public const string Monitor = "monitor";
    public const string Contacted = "contacted";
    public const string Referred = "referred";
    public static readonly string[] All = [NoAction, Monitor, Contacted, Referred];
}

/// <summary>One "mark as reviewed" action on an episode: the outcome, an optional comment and what it resolved.</summary>
public class EpisodeReview
{
    public Guid Id { get; set; }
    public Guid EpisodeId { get; set; }
    public Episode? Episode { get; set; }
    public Guid ReviewedBy { get; set; }
    public User? Reviewer { get; set; }
    /// <summary>no_action | monitor | contacted | referred</summary>
    public required string Status { get; set; }
    public string? Comment { get; set; }
    public DateTime CreatedAt { get; set; }
    public int CheckIns { get; set; }
    public int Logs { get; set; }
    public int FlagsResolved { get; set; }
}

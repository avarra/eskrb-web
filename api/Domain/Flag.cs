namespace Eskrb.Api.Domain;

/// <summary>A red flag raised by a protocol rule on a check-in (later: a log). Only rules create flags.</summary>
public class Flag
{
    public Guid Id { get; set; }
    public Guid EpisodeId { get; set; }
    public Episode? Episode { get; set; }
    /// <summary>checkin | log</summary>
    public required string SourceType { get; set; }
    public Guid SourceId { get; set; }
    public required string RuleId { get; set; }
    /// <summary>attention | urgent</summary>
    public required string Severity { get; set; }
    /// <summary>The answer (or, for logs, the patient's own words) that triggered the rule.</summary>
    public required string Quote { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
}

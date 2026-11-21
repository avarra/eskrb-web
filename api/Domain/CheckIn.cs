using System.Text.Json;

namespace Eskrb.Api.Domain;

/// <summary>One submission of the protocol's daily questionnaire. Several per day are allowed.</summary>
public class CheckIn
{
    public Guid Id { get; set; }
    public Guid EpisodeId { get; set; }
    public Episode? Episode { get; set; }
    /// <summary>Calendar day (Europe/Ljubljana) the check-in belongs to; set by the server.</summary>
    public DateOnly Date { get; set; }
    /// <summary>Keyed by protocol question id.</summary>
    public Dictionary<string, JsonElement> Answers { get; set; } = [];
    /// <summary>Who submitted it (patient now; caretaker later).</summary>
    public Guid AuthoredBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public Guid? ReviewedBy { get; set; }
}

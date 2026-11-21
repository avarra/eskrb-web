using System.Text.Json;

namespace Eskrb.Api.Domain;

/// <summary>Extra patient input outside the check-in: confirmed text (typed or dictated on the phone) or a photo. Never audio.</summary>
public class Log
{
    public Guid Id { get; set; }
    public Guid EpisodeId { get; set; }
    public Episode? Episode { get; set; }
    /// <summary>text | photo</summary>
    public required string Type { get; set; }
    /// <summary>typed | dictated (text logs only)</summary>
    public string? Source { get; set; }
    public string? Text { get; set; }
    /// <summary>Opaque storage key from IMediaStore (photos only). Never a filesystem path.</summary>
    public string? MediaKey { get; set; }
    public string? MediaContentType { get; set; }
    /// <summary>Values the extractor found for protocol question ids (validated); same keys as check-in answers.</summary>
    public Dictionary<string, JsonElement> Extracted { get; set; } = [];
    public Guid AuthoredBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public Guid? ReviewedBy { get; set; }
}

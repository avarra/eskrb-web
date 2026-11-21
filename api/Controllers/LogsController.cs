using System.Text.Json;
using Eskrb.Api.Data;
using Eskrb.Api.Domain;
using Eskrb.Api.Integrations;
using Eskrb.Api.Rules;
using Eskrb.Api.Services;
using Eskrb.Api.Services.Media;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace Eskrb.Api.Controllers;

public record TextLogRequest(string Type, string Source, string Text);
public record LogDto(Guid Id, string Type, string? Source, string? Text, string? MediaUrl, DateTime CreatedAt);
public record LogResultDto(
    LogDto Log, Dictionary<string, JsonElement> Extracted, List<FlagDto> Flags, string PatientAction, bool ExtractionFailed);

/// <summary>
/// Extra patient notes. NO AUDIO: the phone sends text the patient has confirmed (typed or dictated) or a photo.
/// Text goes through extraction + the rules engine; photos are stored and shown, never analysed.
/// </summary>
[ApiController]
[Authorize(Roles = "patient")]
public class LogsController(EskrbDb db, IExtractor extractor, IMediaStore media, ILogger<LogsController> logger) : ControllerBase
{
    private const int MaxTextLength = 4000;
    private const long MaxPhotoBytes = 10_000_000;

    private async Task<(Episode? Ep, ActionResult? Error)> ActiveOwnEpisode(Guid id)
    {
        var ep = await db.OwnEpisode(id, User.UserId());
        if (ep?.Protocol is null) return (null, NotFound());
        if (!ep.IsActive(AppTime.Today)) return (null, Problem(detail: "This episode is not active today.", statusCode: 409));
        return (ep, null);
    }

    [HttpPost("/episodes/{id:guid}/logs")]
    [Consumes("application/json")]
    public async Task<ActionResult<LogResultDto>> AddText(Guid id, TextLogRequest req, CancellationToken ct)
    {
        var (ep, error) = await ActiveOwnEpisode(id);
        if (ep is null) return error!;

        var text = req.Text?.Trim() ?? "";
        if (req.Type != "text") return Problem(detail: "type must be 'text' for a JSON log (photos use multipart).", statusCode: 400);
        if (req.Source is not ("typed" or "dictated")) return Problem(detail: "source must be 'typed' or 'dictated'.", statusCode: 400);
        if (text.Length == 0) return Problem(detail: "text is required.", statusCode: 400);
        if (text.Length > MaxTextLength) return Problem(detail: $"text is longer than {MaxTextLength} characters.", statusCode: 400);

        var now = DateTime.UtcNow;
        var log = new Log { Id = Guid.NewGuid(), EpisodeId = ep.Id, Type = "text", Source = req.Source, Text = text, AuthoredBy = User.UserId(), CreatedAt = now };

        // Extraction only fills fields. If it fails the note is still saved (but cannot raise flags).
        Dictionary<string, ExtractedValue> found = [];
        var failed = false;
        try { found = await extractor.ExtractAsync(ep.Protocol!, text, ct); }
        catch (Exception ex) { failed = true; logger.LogWarning(ex, "Extraction failed for episode {EpisodeId}", ep.Id); }

        var valid = found
            .Where(kv => ep.Protocol!.Questions.FirstOrDefault(q => q.Id == kv.Key) is { } q && AnswerValidator.CheckAnswer(q, kv.Value.Value) is null)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        log.Extracted = valid.ToDictionary(kv => kv.Key, kv => kv.Value.Value);

        var flags = RulesEngine.Evaluate(log.Extracted, ep.Protocol!.Rules).Select(h => new Flag
        {
            Id = Guid.NewGuid(), EpisodeId = ep.Id, SourceType = "log", SourceId = log.Id,
            RuleId = h.RuleId, Severity = h.Severity, Quote = Quotes.Resolve(text, valid[h.Field].Quote), CreatedAt = now,
        }).ToList();

        db.Logs.Add(log);
        db.Flags.AddRange(flags);
        await db.SaveChangesAsync(ct);

        return StatusCode(201, Result(log, flags, failed));
    }

    [HttpPost("/episodes/{id:guid}/logs")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxPhotoBytes + 1_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxPhotoBytes + 1_000_000)]
    public async Task<ActionResult<LogResultDto>> AddPhoto(Guid id, IFormFile file, CancellationToken ct)
    {
        var (ep, error) = await ActiveOwnEpisode(id);
        if (ep is null) return error!;

        if (file.Length == 0 || file.Length > MaxPhotoBytes)
            return Problem(detail: $"Photo must be between 1 byte and {MaxPhotoBytes / 1_000_000} MB.", statusCode: 400);

        var head = new byte[16];
        int read;
        await using (var peek = file.OpenReadStream()) read = await peek.ReadAsync(head, ct);
        var kind = ImageSniffer.Detect(head.AsSpan(0, read));
        if (kind is null) return Problem(detail: "Only JPEG, PNG, WebP or HEIC images are accepted.", statusCode: 400);

        string key;
        await using (var stream = file.OpenReadStream()) key = await media.SaveAsync(stream, kind.Value.Extension, ct);

        var log = new Log
        {
            Id = Guid.NewGuid(), EpisodeId = ep.Id, Type = "photo", MediaKey = key,
            MediaContentType = kind.Value.ContentType, AuthoredBy = User.UserId(), CreatedAt = DateTime.UtcNow,
        };
        db.Logs.Add(log);
        await db.SaveChangesAsync(ct);
        return StatusCode(201, Result(log, [], false));
    }

    private static LogResultDto Result(Log log, List<Flag> flags, bool extractionFailed) => new(
        ToDto(log), log.Extracted,
        flags.Select(f => new FlagDto(f.Id, f.RuleId, f.Severity, f.Quote)).ToList(),
        PatientActions.From(flags.Select(f => f.Severity)), extractionFailed);

    public static LogDto ToDto(Log l) =>
        new(l.Id, l.Type, l.Source, l.Text, l.MediaKey is null ? null : $"/media/{l.Id}", l.CreatedAt);
}

using System.Text.Json;
using Eskrb.Api.Data;
using Eskrb.Api.Domain;
using Eskrb.Api.Integrations;
using Eskrb.Api.Rules;
using Eskrb.Api.Services;
using Eskrb.Api.Services.Media;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Eskrb.Api.Controllers;

public record CheckInRequest(Dictionary<string, JsonElement> Answers);
public record FlagDto(Guid Id, string RuleId, string Severity, string Quote);
public record CheckInResultDto(
    Guid Id, DateOnly Date, Dictionary<string, JsonElement> Answers, List<FlagDto> Flags, string PatientAction);

[ApiController]
public class CheckInsController(EskrbDb db, IMediaStore media, IExtractor extractor, ILogger<CheckInsController> logger) : ControllerBase
{
    private const long MaxPhotoBytes = 10_000_000;

    /// <summary>
    /// Submit a daily check-in for the caller's own episode. Answers are validated against the protocol,
    /// the rules engine (not an LLM) raises flags, and <c>patientAction</c> tells the phone which screen to show.
    /// Several check-ins per day are allowed; the date is set by the server (Europe/Ljubljana).
    /// This JSON form cannot carry photos; protocols with photo questions use the multipart form below.
    /// </summary>
    [HttpPost("/episodes/{id:guid}/checkins")]
    [Authorize(Roles = "patient")]
    [Consumes("application/json")]
    public Task<ActionResult<CheckInResultDto>> Submit(Guid id, CheckInRequest req) =>
        SaveAsync(id, req.Answers ?? [], [], default);

    /// <summary>
    /// Same check-in, multipart: a text field <c>answers</c> holding the JSON object of non-photo answers, plus one
    /// image file part per photo question, named after the question id (max one file per question, 10 MB each).
    /// Everything is validated before anything is stored.
    /// </summary>
    [HttpPost("/episodes/{id:guid}/checkins")]
    [Authorize(Roles = "patient")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(12 * MaxPhotoBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = 12 * MaxPhotoBytes)]
    public async Task<ActionResult<CheckInResultDto>> SubmitMultipart(Guid id, CancellationToken ct)
    {
        var form = await Request.ReadFormAsync(ct);
        Dictionary<string, JsonElement> answers = [];
        if (form.TryGetValue("answers", out var raw) && !string.IsNullOrWhiteSpace(raw.ToString()))
        {
            try { answers = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(raw.ToString()) ?? []; }
            catch (JsonException) { return Problem(detail: "'answers' must be a JSON object.", statusCode: 400); }
        }
        return await SaveAsync(id, answers, form.Files.ToList(), ct);
    }

    private async Task<ActionResult<CheckInResultDto>> SaveAsync(
        Guid id, Dictionary<string, JsonElement> answers, List<IFormFile> files, CancellationToken ct)
    {
        var userId = User.UserId();
        var ep = await db.Episodes.Include(e => e.Protocol)
            .FirstOrDefaultAsync(e => e.Id == id && e.PatientId == userId, ct);
        if (ep?.Protocol is null) return NotFound();
        var protocol = ep.Protocol;

        var today = AppTime.Today;
        if (today < ep.StartDate || today > ep.EndDate)
            return Problem(detail: "This episode is not active today.", statusCode: 409);

        // Photo files: part name = a photo question id, at most one per question, real image bytes only.
        var photoQuestions = protocol.Questions.Where(q => q.Type == "photo").Select(q => q.Id).ToHashSet();
        var photos = new List<(string QuestionId, IFormFile File, (string ContentType, string Extension) Kind)>();
        foreach (var file in files)
        {
            if (!photoQuestions.Contains(file.Name))
                return Problem(detail: $"File part '{file.Name}' is not a photo question of this protocol.", statusCode: 400);
            if (photos.Any(p => p.QuestionId == file.Name))
                return Problem(detail: $"Only one photo per question ('{file.Name}').", statusCode: 400);
            if (file.Length == 0 || file.Length > MaxPhotoBytes)
                return Problem(detail: $"Photo '{file.Name}' must be between 1 byte and {MaxPhotoBytes / 1_000_000} MB.", statusCode: 400);
            var head = new byte[16];
            int read;
            await using (var peek = file.OpenReadStream()) read = await peek.ReadAsync(head, ct);
            var kind = ImageSniffer.Detect(head.AsSpan(0, read));
            if (kind is null)
                return Problem(detail: $"'{file.Name}': only JPEG, PNG, WebP, HEIC or AVIF images are accepted.", statusCode: 400);
            photos.Add((file.Name, file, kind.Value));
        }

        var errors = AnswerValidator.Validate(protocol, answers, photos.Select(p => p.QuestionId).ToHashSet());
        if (errors.Count > 0) return Problem(detail: string.Join(" ", errors), statusCode: 400);

        var now = DateTime.UtcNow;
        var stored = new Dictionary<string, JsonElement>(answers);
        var photoLogs = new List<Log>();
        foreach (var (questionId, file, kind) in photos)
        {
            string key;
            await using (var stream = file.OpenReadStream()) key = await media.SaveAsync(stream, kind.Extension, ct);
            // The photo is stored like any photo log (same authorized /media/{id}); the answer is its opaque URL.
            var log = new Log
            {
                Id = Guid.NewGuid(), EpisodeId = ep.Id, Type = "photo", MediaKey = key,
                MediaContentType = kind.ContentType, AuthoredBy = userId, CreatedAt = now,
            };
            photoLogs.Add(log);
            stored[questionId] = JsonSerializer.SerializeToElement($"/media/{log.Id}");
        }

        var checkIn = new CheckIn
        {
            Id = Guid.NewGuid(), EpisodeId = ep.Id, Date = today,
            Answers = stored, AuthoredBy = userId, CreatedAt = now,
        };

        // Freeform text questions: the LLM fills the structured questions the patient left empty (never ones marked
        // skipExtraction, never photos/text). It only proposes values; the rules below still decide every flag.
        var inferred = await InferFromFreeTextAsync(ep, protocol, answers, ct);
        var ruleInput = new Dictionary<string, JsonElement>(answers);
        foreach (var (qid, v) in inferred) { ruleInput[qid] = v.Value; stored[qid] = v.Value; }

        var flags = RulesEngine.Evaluate(ruleInput, protocol.Rules)
            .Select(h => new Flag
            {
                Id = Guid.NewGuid(), EpisodeId = ep.Id, SourceType = "checkin", SourceId = checkIn.Id,
                RuleId = h.RuleId, Severity = h.Severity, CreatedAt = now,
                Quote = inferred.TryGetValue(h.Field, out var iv)
                    ? Quotes.Resolve(string.Join("\n", FreeTexts(protocol, answers)), iv.Quote)
                    : $"{h.Field}: {Render(protocol.Questions.FirstOrDefault(q => q.Id == h.Field), h.Answer)}",
            }).ToList();

        db.Logs.AddRange(photoLogs);
        db.CheckIns.Add(checkIn);
        db.Flags.AddRange(flags);
        await db.SaveChangesAsync(ct);

        return StatusCode(201, new CheckInResultDto(
            checkIn.Id, checkIn.Date, checkIn.Answers,
            flags.Select(f => new FlagDto(f.Id, f.RuleId, f.Severity, f.Quote)).ToList(),
            PatientActions.From(flags.Select(f => f.Severity))));
    }

    private static IEnumerable<string> FreeTexts(Protocol p, Dictionary<string, JsonElement> answers) =>
        p.Questions.Where(q => q.Type == "text" && answers.ContainsKey(q.Id)).Select(q => answers[q.Id].GetString()!);

    private async Task<Dictionary<string, ExtractedValue>> InferFromFreeTextAsync(
        Episode ep, Protocol protocol, Dictionary<string, JsonElement> answers, CancellationToken ct)
    {
        var text = string.Join("\n", FreeTexts(protocol, answers));
        if (text.Length == 0) return [];
        var targets = protocol.Questions
            .Where(q => q.Type is not ("photo" or "text") && !q.SkipExtraction && !answers.ContainsKey(q.Id)).ToList();
        if (targets.Count == 0) return [];
        try
        {
            var found = await extractor.ExtractAsync(
                new Protocol { Id = protocol.Id, Name = protocol.Name, DurationDays = protocol.DurationDays, Questions = targets }, text, ct);
            return found.Where(kv => targets.FirstOrDefault(q => q.Id == kv.Key) is { } q && AnswerValidator.CheckAnswer(q, kv.Value.Value) is null)
                .ToDictionary(kv => kv.Key, kv => kv.Value);
        }
        catch (Exception ex) { logger.LogWarning(ex, "Check-in extraction failed for episode {EpisodeId}", ep.Id); return []; }
    }

    /// <summary>Flag quote for a structured answer: option ids are shown as their Slovenian label when the question has one.</summary>
    private static string Render(ProtocolQuestion? q, JsonElement a)
    {
        string One(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.String when q?.Labels?.TryGetValue(e.GetString()!, out var l) == true => l,
            _ => e.ToString(),
        };
        return a.ValueKind == JsonValueKind.Array ? string.Join(", ", a.EnumerateArray().Select(One)) : One(a);
    }
}

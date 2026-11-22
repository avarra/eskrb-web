using System.Text.Json;
using Eskrb.Api.Data;
using Eskrb.Api.Integrations;
using Eskrb.Api.Rules;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Eskrb.Api.Controllers;

public record ExtractDemoRequest(string Text, string? ProtocolId);
public record ExtractDemoFlag(string RuleId, string Severity, string Quote);
public record ExtractDemoResult(
    string Extractor, string ProtocolId, Dictionary<string, JsonElement> Extracted, Dictionary<string, string> Quotes,
    List<ExtractDemoFlag> Flags, string PatientAction, long ElapsedMs);

/// <summary>Staff/admin sandbox for the LLM pipeline: runs extraction + rules on a note WITHOUT saving anything.</summary>
[ApiController]
[Authorize(Roles = "staff,admin")]
public class DemoController(EskrbDb db, IExtractor extractor, ILogger<DemoController> logger) : ControllerBase
{
    /// <summary>Try the note-to-flags pipeline. Nothing is stored; the text is never logged. Uses the real LLM when configured.</summary>
    [HttpPost("/demo/extract")]
    [EnableRateLimiting("llm")]
    public async Task<ActionResult<ExtractDemoResult>> Extract(ExtractDemoRequest req, CancellationToken ct)
    {
        var text = req.Text?.Trim() ?? "";
        if (text.Length == 0 || text.Length > 4000) return Problem(statusCode: 400, detail: "text must be 1-4000 characters.");

        var protocol = await db.Protocols.FindAsync([req.ProtocolId ?? "knee-replacement"], ct);
        if (protocol is null) return Problem(statusCode: 404, detail: "Unknown protocolId.");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Dictionary<string, ExtractedValue> found;
        try { found = await extractor.ExtractAsync(protocol, text, ct); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Demo extraction failed");
            return Problem(statusCode: 502, detail: $"Extraction failed: {ex.Message}");
        }

        var valid = found
            .Where(kv => protocol.Questions.FirstOrDefault(q => q.Id == kv.Key) is { } q && AnswerValidator.CheckAnswer(q, kv.Value.Value) is null)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        var values = valid.ToDictionary(kv => kv.Key, kv => kv.Value.Value);
        var hits = RulesEngine.Evaluate(values, protocol.Rules);
        var flags = hits.Select(h => new ExtractDemoFlag(h.RuleId, h.Severity, Quotes.Resolve(text, valid[h.Field].Quote))).ToList();

        return new ExtractDemoResult(
            extractor.GetType().Name, protocol.Id, values, valid.ToDictionary(kv => kv.Key, kv => kv.Value.Quote),
            flags, PatientActions.From(hits.Select(h => h.Severity)), sw.ElapsedMilliseconds);
    }
}

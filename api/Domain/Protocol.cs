using System.Text.Json;

namespace Eskrb.Api.Domain;

/// <summary>Global (not per-hospital) recovery program. Questions and rules are data, not code.</summary>
public class Protocol
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public int DurationDays { get; set; }
    public List<ProtocolQuestion> Questions { get; set; } = [];
    public List<ProtocolRule> Rules { get; set; } = [];
}

public class ProtocolQuestion
{
    public required string Id { get; set; }
    /// <summary>scale | number | choice | boolean | photo | text (freeform, max 2000 chars; the LLM extracts the other questions from it)</summary>
    public required string Type { get; set; }
    public required string Text { get; set; }
    public int? Min { get; set; }
    public int? Max { get; set; }
    public string? Unit { get; set; }
    public List<string>? Options { get; set; }
    /// <summary>A check-in is rejected unless this question is answered (for photo questions: a file is attached).</summary>
    public bool Required { get; set; }
    /// <summary>True = purely rule-based question: the LLM never fills it from free text (it must be answered directly).</summary>
    public bool SkipExtraction { get; set; }
}

public class ProtocolRule
{
    public required string Id { get; set; }
    public required string Field { get; set; }
    public required string Op { get; set; }
    /// <summary>Number, string or bool depending on the question type.</summary>
    public JsonElement Value { get; set; }
    /// <summary>attention | urgent</summary>
    public required string Severity { get; set; }
}
